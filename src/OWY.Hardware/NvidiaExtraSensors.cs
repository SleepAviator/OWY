using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using OWY.Core;

namespace OWY.Hardware;

// Independent declarations of two driver getters whose ABI/channel mapping is
// recorded in LHM v0.9.6. No LHM or NVIDIA binary is bundled. Restricted to the
// tested RTX 50 family, visible details, and fresh OS activity permission.
public sealed class NvidiaExtraSensors(Func<bool> allowed) : IDisposable
{
    nint library,device;bool initialized;long retryAt;string name="";string? failure;
    QueryInterface? query;Simple? unload;BufferQuery? temperatures,voltage;
    public DeviceReading Latest {get;private set;}=DeviceReading.Empty;
    public bool Initialized=>initialized;
    public long Queries {get;private set;}
    public long Connections {get;private set;}
    public long Releases {get;private set;}
    void Guard(){if(!allowed())throw new NvidiaPollingDeniedException();}
    T Resolve<T>(uint id) where T:Delegate
    {
        Guard();nint address=query!(id);
        if(address==0)throw new InvalidDataException("NVAPI sensor interface unavailable (0x"+id.ToString("x")+")");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }
    static void Require(int status,string operation)
    {if(status!=0)throw new InvalidDataException("NVAPI "+operation+" failed ("+status+")");}
    public DeviceReading Sample(GpuSelection selected,DateTimeOffset now)
    {
        if(!selected.AllowNvidiaSensors || !allowed()){Clear();return Latest;}
        if(!selected.Name.StartsWith("NVIDIA GeForce RTX 50",StringComparison.OrdinalIgnoreCase))
        {Clear();return Latest=new([],"Extra NVIDIA sensor mapping is unavailable for this GPU family");}
        if(Environment.TickCount64<retryAt)return Latest=new([],failure);
        try
        {
            if(initialized && name!=selected.Name)Clear();
            if(!initialized)Open(selected.Name);
            byte[] thermal=new byte[168];Set(thermal,0,168|2u<<16);Set(thermal,4,4); // memory channel only
            Guard();int status=temperatures!(device,thermal);Queries++;Require(status,"memory temperature");
            byte[] volts=new byte[76];Set(volts,0,76|1u<<16);
            Guard();status=voltage!(device,volts);Queries++;Require(status,"core voltage");
            Guard();failure=null;return Latest=new(Parse(thermal,volts,now),null);
        }
        catch(NvidiaPollingDeniedException){Clear();return Latest;}
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidDataException)
        {
            string issue="Extra NVIDIA sensors unavailable: "+ex.Message;
            try{Clear();}catch(InvalidDataException close){issue+="; "+close.Message;}
            retryAt=Environment.TickCount64+60000;failure=issue;return Latest=new([],issue);
        }
    }
    void Open(string expectedName)
    {
        Guard();library=NativeLibrary.Load(Path.Combine(Environment.SystemDirectory,"nvapi64.dll"));
        Guard();query=Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(library,"nvapi_QueryInterface"));
        var initialize=Resolve<Simple>(0x0150e828);unload=Resolve<Simple>(0xd22bdd7e);
        Guard();Require(initialize(),"initialization");initialized=true;Connections++;
        var enumerate=Resolve<Enumerate>(0xe5ac921f);var getName=Resolve<NameQuery>(0xceee8e9f);
        var devices=new nint[64];Guard();Require(enumerate(devices,out int count),"enumeration");
        if(count!=1 || devices[0]==0)throw new InvalidDataException("NVAPI GPU identity unavailable or ambiguous");
        device=devices[0];var text=new StringBuilder(64);Guard();Require(getName(device,text),"GPU name");
        if(!string.Equals("NVIDIA "+text,expectedName,StringComparison.OrdinalIgnoreCase) && !string.Equals(text.ToString(),expectedName,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NVAPI GPU differs from Windows selection");
        temperatures=Resolve<BufferQuery>(0x65fe3aad);voltage=Resolve<BufferQuery>(0x465f9bcf);
        name=expectedName;
    }
    public static SensorValue[] Parse(byte[] thermal,byte[] voltage,DateTimeOffset now)
    {
        if(thermal.Length!=168 || voltage.Length!=76 || U32(thermal,0)!=(168|2u<<16) || (U32(thermal,4)&4)==0 || U32(voltage,0)!=(76|1u<<16))
            throw new InvalidDataException("Invalid NVAPI sensor layout");
        // RTX 50 channel 2 is memory junction; channel 1 is die, not hotspot.
        double temp=BinaryPrimitives.ReadInt32LittleEndian(thermal.AsSpan(48,4))/256.0;
        double volts=U32(voltage,40)/1_000_000.0;
        double? t=temp is >0 and <=150?temp:null,v=volts is >0 and <=2?volts:null;
        return [new("nvidia:memory:temperature","NVIDIA VRAM junction",t,"°C",now,t.HasValue?"fresh":"unavailable","NVIDIA driver NVAPI"),
            new("nvidia:core:voltage","NVIDIA core voltage",v,"V",now,v.HasValue?"fresh":"unavailable","NVIDIA driver NVAPI")];
    }
    static uint U32(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));
    static void Set(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
    public void Clear()
    {
        try{if(initialized){initialized=false;Releases++;Require(unload!(),"shutdown");}}
        finally
        {
            device=0;name="";Latest=DeviceReading.Empty;query=null;unload=null;temperatures=null;voltage=null;
            if(library!=0){NativeLibrary.Free(library);library=0;}
        }
    }
    public void Dispose()=>Clear();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate nint QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Simple();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Enumerate([Out] nint[] devices,out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl,CharSet=CharSet.Ansi)]delegate int NameQuery(nint device,StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int BufferQuery(nint device,[In,Out] byte[] data);
}
