using System.Runtime.InteropServices;
using System.Text;
using OWY.Core;

namespace OWY.Hardware;

public sealed record NvidiaReading(string Name,SensorValue[] Sensors,string? Issue,DateTimeOffset? SampledAt)
{
    public static NvidiaReading Empty=>new("NVIDIA GPU",[],null,null);
    public double? BoardWatts
    {
        get
        {
            var values=Sensors.Where(s=>s.Id=="nvidia:board:power").ToArray();
            return values.Length==1 && values[0] is {Status:"fresh",Unit:"W",Value:{} value}
                && double.IsFinite(value) && value is >=0 and <=1000?value:null;
        }
    }
    public NvidiaReading Expire(DateTimeOffset now,TimeSpan maximumAge)=>SampledAt is {} at && (now<at || now-at>maximumAge)
        ?this with{Sensors=Sensors.Select(s=>s with{Value=null,Status="stale"}).ToArray(),Issue="NVIDIA readings are stale"}:this;
}

// A narrow driver boundary makes the no-I/O sleep guard testable without waking hardware.
public interface INvidiaApi
{
    void Open(string expectedName);
    NvidiaReading Read(DateTimeOffset now);
    void Close();
}

public sealed class NvidiaSensors : IDisposable
{
    readonly Func<bool> allowed;readonly INvidiaApi api;
    bool initialized,engaged;string deviceName="";long retryAt;
    public NvidiaReading Latest {get;private set;}=NvidiaReading.Empty;
    public long Connections {get;private set;}
    public long Samples {get;private set;}
    public long Releases {get;private set;}
    public bool Initialized=>initialized;
    public NvidiaSensors(Func<bool> allowed,INvidiaApi? api=null)
    {this.allowed=allowed;this.api=api??new NvmlApi(allowed);}
    public NvidiaReading Sample(GpuSelection selected,DateTimeOffset now)
    {
        if(!selected.AllowNvidiaSensors || !allowed()){Clear();return Latest;}
        if(Environment.TickCount64<retryAt)return Latest;
        try
        {
            if(initialized && deviceName!=selected.Name)Release();
            if(!initialized)
            {
                // Guard before the first P/Invoke, enumeration and device handle.
                if(!allowed())throw new NvidiaPollingDeniedException();
                engaged=true;api.Open(selected.Name);initialized=true;deviceName=selected.Name;Connections++;
            }
            if(!allowed())throw new NvidiaPollingDeniedException();
            var value=api.Read(now);
            if(!allowed())throw new NvidiaPollingDeniedException();
            Samples++;return Latest=value;
        }
        catch(NvidiaPollingDeniedException){Clear();return Latest;}
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidDataException)
        {
            string issue=ex is InvalidDataException?ex.Message:"NVIDIA NVML unavailable ("+ex.GetType().Name+")";
            try{Release();}catch(InvalidDataException close){issue+="; "+close.Message;}
            retryAt=Environment.TickCount64+60000;
            return Latest=new(selected.Name,[],issue,now);
        }
    }
    void Release()
    {
        if(!engaged)return;
        engaged=false;
        bool wasOpen=initialized;initialized=false;deviceName="";
        try{api.Close();}finally{if(wasOpen)Releases++;}
    }
    public void Clear()
    {
        retryAt=0;
        try{Release();Latest=NvidiaReading.Empty;}
        catch(InvalidDataException ex){Latest=new("NVIDIA GPU",[],ex.Message,null);}
    }
    public void Dispose()=>Release();
}

sealed class NvidiaPollingDeniedException : Exception {}

// Use the installed driver's documented NVML API. No NVIDIA DLL is redistributed.
// This first local release accepts one NVIDIA board, matching Windows's selected name.
sealed class NvmlApi(Func<bool> allowed) : INvidiaApi
{
    const int NotSupported=3;
    bool initialized;nint device;string name="NVIDIA GPU";
    void Guard(){if(!allowed())throw new NvidiaPollingDeniedException();}
    static void Require(int status,string operation)
    {if(status!=0)throw new InvalidDataException("NVIDIA NVML "+operation+" failed ("+status+")");}
    public void Open(string expectedName)
    {
        Guard();Require(Native.nvmlInit_v2(),"initialization");initialized=true;
        Guard();Require(Native.nvmlDeviceGetCount_v2(out uint count),"enumeration");
        if(count!=1)throw new InvalidDataException("NVIDIA board identity is unavailable or ambiguous");
        Guard();Require(Native.nvmlDeviceGetHandleByIndex_v2(0,out device),"device handle");
        Guard();var text=new StringBuilder(96);Require(Native.nvmlDeviceGetName(device,text,(uint)text.Capacity),"device name");
        name=text.ToString();
        if(!string.Equals(name,expectedName,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NVIDIA board does not match Windows's active GPU");
    }
    public NvidiaReading Read(DateTimeOffset now)
    {
        var sensors=new List<SensorValue>();
        void Add(string id,string label,double? value,string unit,double maximum,string status="fresh")
        {
            if(value is {} n && (!double.IsFinite(n) || n<0 || n>maximum)){value=null;status="invalid";}
            sensors.Add(new("nvidia:"+id,"NVIDIA "+label,value,unit,now,value.HasValue?"fresh":status,"NVIDIA NVML"));
        }
        void Query(string id,string label,string unit,double maximum,Func<(int Status,uint Value)> query,double divisor=1)
        {
            Guard();var result=query();
            if(result.Status==NotSupported){Add(id,label,null,unit,maximum,"unsupported");return;}
            Require(result.Status,label);Add(id,label,result.Value/divisor,unit,maximum);
        }
        Query("board:power","board power","W",1000,()=>{int code=Native.nvmlDeviceGetPowerUsage(device,out uint value);return(code,value);},1000);
        Query("temperature","GPU temperature","°C",150,()=>{int code=Native.nvmlDeviceGetTemperature(device,0,out uint value);return(code,value);});
        foreach(var (kind,key,label) in new[]{(0u,"graphics","graphics clock"),(2u,"memory","memory clock"),(3u,"video","video clock")})
            Query("clock:"+key,label,"MHz",30000,()=>{int code=Native.nvmlDeviceGetClockInfo(device,kind,out uint value);return(code,value);});
        Guard();int status=Native.nvmlDeviceGetUtilizationRates(device,out var utilization);
        if(status==NotSupported){Add("load","GPU load",null,"%",100,"unsupported");Add("memory:load","memory controller load",null,"%",100,"unsupported");}
        else{Require(status,"utilization");Add("load","GPU load",utilization.Gpu,"%",100);Add("memory:load","memory controller load",utilization.Memory,"%",100);}
        Guard();status=Native.nvmlDeviceGetMemoryInfo(device,out var memory);
        if(status==NotSupported)Add("memory:used","VRAM used",null,"bytes",1e15,"unsupported");
        else
        {
            Require(status,"VRAM use");
            Add("memory:used","VRAM used",memory.Used<=memory.Total && memory.Free<=memory.Total?memory.Used:null,"bytes",1e15);
            Add("memory:total","VRAM total",memory.Total,"bytes",1e15);
        }
        return new(name,sensors.ToArray(),sensors[0].Value.HasValue?null:"NVIDIA board power is unavailable",now);
    }
    public void Close()
    {
        device=0;
        if(!initialized)return;
        initialized=false;Require(Native.nvmlShutdown(),"shutdown");
    }
    static class Native
    {
        const string Library="nvml.dll";
        [StructLayout(LayoutKind.Sequential)]public struct Utilization {public uint Gpu,Memory;}
        [StructLayout(LayoutKind.Sequential)]public struct Memory {public ulong Total,Free,Used;}
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlInit_v2();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlShutdown();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlDeviceGetHandleByIndex_v2(uint index,out nint device);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlDeviceGetName(nint device,StringBuilder name,uint length);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlDeviceGetPowerUsage(nint device,out uint milliwatts);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlDeviceGetTemperature(nint device,uint sensor,out uint degrees);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlDeviceGetClockInfo(nint device,uint kind,out uint megahertz);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlDeviceGetUtilizationRates(nint device,out Utilization utilization);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]public static extern int nvmlDeviceGetMemoryInfo(nint device,out Memory memory);
    }
}
