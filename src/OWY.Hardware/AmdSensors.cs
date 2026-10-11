using System.Runtime.InteropServices;
using OWY.Core;

namespace OWY.Hardware;

public sealed record AmdReading(string Name,double? PackageWatts,double? GraphicsWatts,SensorValue[] Sensors,string? Issue)
{
    public static AmdReading Empty=>new("AMD APU",null,null,[],null);
    public static AmdReading FromMetrics(string name,IReadOnlyDictionary<int,int> metrics,DateTimeOffset now)
    {
        var sensors=new List<SensorValue>();
        double? Get(int id,double max)=>metrics.TryGetValue(id,out int n) && n>=0 && n<=max?n:null;
        void Add(int id,string key,string label,string unit,double max,bool required=false)
        {
            var value=Get(id,max);
            if(value.HasValue || required)sensors.Add(new("amd:"+key,label,value,unit,now,value.HasValue?"fresh":"unavailable","AMD ADL PMLog"));
        }
        Add(23,"apu:power","APU package (CPU + iGPU)","W",1000,true);
        Add(30,"igpu:power","iGPU graphics power","W",1000,true);
        Add(32,"cpu:temperature","CPU temperature","°C",150,true);
        Add(28,"igpu:temperature","iGPU temperature","°C",150,true);
        Add(1,"igpu:clock","iGPU graphics clock","MHz",15000,true);
        Add(19,"igpu:load","iGPU load","%",100,true);
        Add(34,"cpu:clock","CPU aggregate clock","MHz",15000);
        Add(33,"cpu:power","CPU domain (included in APU)","W",1000);
        Add(2,"memory:clock","AMD memory clock domain","MHz",15000);
        Add(3,"soc:clock","SoC clock","MHz",15000);
        Add(7,"video:clock","Video clock","MHz",15000);
        Add(29,"soc:temperature","SoC temperature","°C",150);
        Add(71,"npu:clock","NPU clock","MHz",15000);
        Add(72,"npu:load","NPU average busy","%",100);
        // These domains overlap. Only package + a separate dGPU board may be
        // summed; memory-domain MHz is not the LPDDR data rate or RAM watts.
        return new(name,Get(23,1000),Get(30,1000),sensors.ToArray(),null);
    }
}

// Read the installed AMD driver's public ADL API, using one physical APU.
// No GPU routing setters, NVIDIA libraries, sensor drivers or logging services.
public sealed class AmdSensors : IDisposable
{
    nint context;int adapter;string name="AMD APU";long retryAt;bool initialized;
    readonly List<nint> allocations=[];readonly Allocate allocate;
    public AmdReading Latest {get;private set;}=AmdReading.Empty;
    public AmdSensors(){allocate=AllocateMemory;}
    nint AllocateMemory(int size)
    {
        if(size<=0 || size>16*1024*1024)return 0;
        nint memory=Marshal.AllocHGlobal(size);lock(allocations)allocations.Add(memory);return memory;
    }
    public void Clear()
    {
        Latest=AmdReading.Empty;
        // The driver context has its own background work. Release it when
        // locked or when battery discharge makes component sampling redundant.
        // A later Sample reconnects through the existing Initialize path.
        if(context!=0)Release();
    }
    public AmdReading Sample(DateTimeOffset now)
    {
        if(Environment.TickCount64<retryAt)return Latest;
        try
        {
            if(!initialized)Initialize();
            var values=new PMLog{Size=Marshal.SizeOf<PMLog>(),Sensors=new Sensor[256]};
            int status=ADL2_New_QueryPMLogData_Get(context,adapter,ref values);
            if(status!=0)throw new InvalidDataException("AMD APU metrics unavailable (ADL "+status+")");
            var metrics=values.Sensors.Select((s,id)=>(s,id)).Where(x=>x.s.Supported!=0).ToDictionary(x=>x.id,x=>x.s.Value);
            return Latest=AmdReading.FromMetrics(name,metrics,now);
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidDataException)
        {
            Latest=new(name,null,null,[],ex.Message);retryAt=Environment.TickCount64+60000;Release();return Latest;
        }
    }
    void Initialize()
    {
        int status=ADL2_Main_Control_Create(allocate,1,out context);
        if(status!=0 || context==0)throw new InvalidDataException("AMD driver sensor context unavailable (ADL "+status+")");
        status=ADL2_Adapter_NumberOfAdapters_Get(context,out int count);
        if(status!=0 || count is <=0 or >64)throw new InvalidDataException("AMD adapter enumeration unavailable (ADL "+status+")");
        int size=Marshal.SizeOf<AdapterInfo>();nint buffer=Marshal.AllocHGlobal(size*count);
        try
        {
            for(int i=0;i<count;i++)Marshal.WriteInt32(buffer+i*size,size);
            status=ADL2_Adapter_AdapterInfo_Get(context,buffer,size*count);
            if(status!=0)throw new InvalidDataException("AMD adapter metadata unavailable (ADL "+status+")");
            var candidates=new List<AdapterInfo>();
            for(int i=0;i<count;i++)
            {
                var value=Marshal.PtrToStructure<AdapterInfo>(buffer+i*size);
                if(!value.Pnp.Contains("VEN_1002",StringComparison.OrdinalIgnoreCase))continue;
                status=ADL2_Adapter_ASICFamilyType_Get(context,value.Index,out int family,out int valid);
                if(status==0 && (family&valid&2)!=0)candidates.Add(value);
            }
            var physical=candidates.GroupBy(a=>(a.Bus,a.Device,a.Function)).ToArray();
            if(physical.Length!=1)throw new InvalidDataException("AMD APU sensor identity is unavailable or ambiguous");
            var selected=physical[0].First();adapter=selected.Index;name=selected.Name;initialized=true;
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    void Release()
    {
        try{if(context!=0 && ADL2_Main_Control_Destroy(context)!=0)throw new InvalidDataException("AMD sensor context could not be closed");}
        finally
        {
            context=0;initialized=false;
            lock(allocations){foreach(var memory in allocations)Marshal.FreeHGlobal(memory);allocations.Clear();}
        }
    }
    public void Dispose(){Release();GC.KeepAlive(allocate);}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate nint Allocate(int bytes);
    [StructLayout(LayoutKind.Sequential)]struct Sensor{public int Supported,Value;}
    [StructLayout(LayoutKind.Sequential)]struct PMLog{public int Size;[MarshalAs(UnmanagedType.ByValArray,SizeConst=256)]public Sensor[] Sensors;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Ansi)]struct AdapterInfo
    {
        public int Size,Index;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Udid;
        public int Bus,Device,Function,Vendor;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Name;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Display;public int Present,Exist;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Driver;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string DriverExtension;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Pnp;public int OsDisplayIndex;
    }
    const string Library="atiadlxx.dll";
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int ADL2_Main_Control_Create(Allocate callback,int connected,out nint context);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int ADL2_Main_Control_Destroy(nint context);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int ADL2_Adapter_NumberOfAdapters_Get(nint context,out int count);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int ADL2_Adapter_AdapterInfo_Get(nint context,nint info,int size);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int ADL2_Adapter_ASICFamilyType_Get(nint context,int adapter,out int family,out int valid);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int ADL2_New_QueryPMLogData_Get(nint context,int adapter,ref PMLog values);
}
