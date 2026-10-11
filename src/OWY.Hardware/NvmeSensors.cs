using System.Buffers.Binary;
using System.ComponentModel;
using System.Numerics;
using Microsoft.Win32.SafeHandles;
using OWY.Core;

namespace OWY.Hardware;

public sealed class NvmeSensors : IDisposable
{
    readonly List<SafeFileHandle> disks=[];long retryAt;
    public DeviceReading Latest {get;private set;}=DeviceReading.Empty;
    public long Queries {get;private set;}
    public bool Connected=>disks.Count>0;
    public DeviceReading Sample(DateTimeOffset now)
    {
        if(Environment.TickCount64<retryAt)return Latest;
        try
        {
            if(disks.Count==0)Connect();var sensors=new List<SensorValue>();
            for(int i=0;i<disks.Count;i++)
            {
                // Device protocol-specific property (49), NVMe (3), log page (2),
                // SMART/health (2). No passthrough commands or write IOCTLs.
                byte[] request=new byte[560];
                Set(request,0,49);Set(request,8,3);Set(request,12,2);Set(request,16,2);Set(request,24,40);Set(request,28,512);
                byte[] reply=DeviceIo.Query(disks[i],0x2d1400,request,560);Queries++;
                sensors.AddRange(Parse(reply,i+1,now));
            }
            return Latest=new(sensors.ToArray(),null);
        }
        catch(Exception ex) when(ex is Win32Exception or InvalidDataException)
        {
            Clear();retryAt=Environment.TickCount64+60000;return Latest=new([],"NVMe health unavailable: "+ex.Message);
        }
    }
    void Connect()
    {
        foreach(string path in DeviceIo.Paths(new("53f56307-b6bf-11d0-94f2-00a0c91efb8b")))
        {
            var device=DeviceIo.Open(path,0);
            try
            {
                byte[] descriptor=DeviceIo.Query(device,0x2d1400,new byte[12],1024);
                if(descriptor.Length<32)throw new InvalidDataException("Short storage descriptor");
                // STORAGE_DEVICE_DESCRIPTOR.BusType == BusTypeNvme (17).
                if(U32(descriptor,28)!=17)continue;
                disks.Add(device);device=null!;
                if(disks.Count>16)throw new InvalidDataException("Too many NVMe devices");
            }
            finally{device?.Dispose();}
        }
        if(disks.Count==0)throw new InvalidDataException("No NVMe health interface found");
    }
    public static SensorValue[] Parse(byte[] reply,int index,DateTimeOffset now)
    {
        if(reply.Length<48 || U32(reply,0)!=48 || U32(reply,4)!=48 || U32(reply,8)!=3 || U32(reply,12)!=2)
            throw new InvalidDataException("Invalid NVMe protocol descriptor");
        uint offset=U32(reply,24),length=U32(reply,28);
        if(offset<40 || length<512 || offset>int.MaxValue-8 || (ulong)offset+8+length>(ulong)reply.Length)
            throw new InvalidDataException("Invalid NVMe log boundaries");
        var log=reply.AsSpan((int)offset+8,512);var sensors=new List<SensorValue>();
        string key="nvme:"+index+":",label="NVMe "+index+" ";
        void Add(string id,string name,double? value,string unit)=>sensors.Add(new(key+id,label+name,value,unit,now,value.HasValue?"fresh":"unavailable","NVMe SMART/health log"));
        ushort composite=BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        Add("temperature","temperature",Temperature(composite),"°C");
        for(int i=0;i<8;i++)
        {
            ushort kelvin=BinaryPrimitives.ReadUInt16LittleEndian(log[(200+2*i)..]);
            if(kelvin!=composite && Temperature(kelvin) is {} temp)Add("temperature:"+(i+1),"temperature sensor "+(i+1),temp,"°C");
        }
        Add("spare","available spare",log[3]<=100?log[3]:null,"%");
        Add("endurance","rated endurance used",log[5],"%");
        Add("read","lifetime read",Counter(log,32)*512000,"bytes");
        Add("write","lifetime written",Counter(log,48)*512000,"bytes");
        Add("cycles","power cycles",Counter(log,112),"cycles");
        Add("hours","power-on time",Counter(log,128),"hours");
        Add("unsafe","unsafe shutdowns",Counter(log,144),"count");
        Add("errors","media errors",Counter(log,160),"count");
        Add("warning","critical warning flags",log[0],"flags");
        return sensors.ToArray();
    }
    static double Counter(ReadOnlySpan<byte> log,int offset)=>(double)new BigInteger(log.Slice(offset,16),isUnsigned:true,isBigEndian:false);
    static double? Temperature(ushort kelvin)=>kelvin is >0 and <65535 && kelvin-273.15 is >=-40 and <=150?kelvin-273.15:null;
    static uint U32(byte[] data,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at,4));
    static void Set(byte[] data,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at,4),value);
    public void Clear(){foreach(var disk in disks)disk.Dispose();disks.Clear();Latest=DeviceReading.Empty;}
    public void Dispose()=>Clear();
}
