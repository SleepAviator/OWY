using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using OWY.Core;

namespace OWY.Hardware;

public sealed class BatterySensors : IDisposable
{
    SafeFileHandle? handle;uint tag;byte[]? information;long retryAt,nextInformation;
    public DeviceReading Latest {get;private set;}=DeviceReading.Empty;
    public long Queries {get;private set;}
    public bool Connected=>handle is {IsClosed:false,IsInvalid:false};
    public DeviceReading Sample(DateTimeOffset now)
    {
        if(Environment.TickCount64<retryAt)return Latest;
        try
        {
            if(handle is null)
            {
                var paths=DeviceIo.Paths(new("72631e54-78a4-11d0-bcf7-00aa00b7b32a")).ToArray();
                if(paths.Length!=1)throw new InvalidDataException("Battery identity unavailable or ambiguous");
                handle=DeviceIo.Open(paths[0],0x80000000);
                byte[] reply=Query(0x294040,new byte[4],4);
                if(reply.Length!=4 || (tag=U32(reply,0))==0)throw new InvalidDataException("Battery tag unavailable");
            }
            if(information is null || Environment.TickCount64>=nextInformation)
            {
                byte[] request=new byte[12];Set(request,0,tag);
                information=Query(0x294044,request,36);nextInformation=Environment.TickCount64+300000;
            }
            byte[] statusRequest=new byte[20];Set(statusRequest,0,tag);Set(statusRequest,16,uint.MaxValue);
            byte[] status=Query(0x29404c,statusRequest,16);
            return Latest=new(Parse(information,status,now),null);
        }
        catch(Exception ex) when(ex is Win32Exception or InvalidDataException)
        {
            Clear();retryAt=Environment.TickCount64+60000;return Latest=new([],"Battery details unavailable: "+ex.Message);
        }
    }
    byte[] Query(uint code,byte[] request,int length){Queries++;return DeviceIo.Query(handle!,code,request,length);}
    public static SensorValue[] Parse(byte[] information,byte[] status,DateTimeOffset now)
    {
        if(information.Length!=36 || status.Length!=16)throw new InvalidDataException("Invalid battery reply length");
        uint capabilities=U32(information,0),state=U32(status,0);
        if((capabilities&0x80000000)==0)throw new InvalidDataException("Device is not a system battery");
        bool relative=(capabilities&0x40000000)!=0;
        double? Capacity(byte[] data,int at)=>U32(data,at) is >0 and <uint.MaxValue && !relative?U32(data,at)/1000.0:null;
        double? designed=Capacity(information,12),full=Capacity(information,16);
        var sensors=new List<SensorValue>();
        void Add(string key,string label,double? value,string unit)=>sensors.Add(new("battery:"+key,"Battery "+label,value,unit,now,value.HasValue?"fresh":"unavailable","Windows battery class driver"));
        Add("design","design capacity",designed,"Wh");Add("full","full-charge capacity",full,"Wh");
        Add("wear","wear",designed is >0 && full is >=0?Math.Clamp(100*(1-full.Value/designed.Value),0,100):null,"%");
        Add("cycles","cycles",U32(information,32) is <uint.MaxValue?U32(information,32):null,"cycles");
        uint voltage=U32(status,8);Add("voltage","voltage",voltage is >0 and <=100000?voltage/1000.0:null,"V");
        uint remaining=U32(status,4);Add("remaining","remaining capacity",!relative && remaining<uint.MaxValue?remaining/1000.0:null,"Wh");
        int rate=BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(12,4));
        Add("discharge","discharge power",!relative?WindowsSensors.DischargeWatts((state&1)!=0,(state&4)!=0,(state&2)!=0,rate):null,"W");
        return sensors.ToArray();
    }
    static uint U32(byte[] data,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at,4));
    static void Set(byte[] data,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at,4),value);
    public void Clear(){handle?.Dispose();handle=null;tag=0;information=null;Latest=DeviceReading.Empty;}
    public void Dispose()=>Clear();
}
