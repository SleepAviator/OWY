using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OWY.Core;

namespace OWY.Hardware;

// Independent getter-only implementation of the Blade HID wire protocol.
// 0x0d88 returns actual RPM; 0x0d81 is a setpoint and is never used as RPM.
public sealed class BladeFanSensors : IDisposable
{
    SafeFileHandle? handle;long retryAt;
    public DeviceReading Latest {get;private set;}=DeviceReading.Empty;
    public long Exchanges {get;private set;}
    public bool Connected=>handle is {IsClosed:false,IsInvalid:false};
    public DeviceReading Sample(Func<bool> permitted)
    {
        if(!permitted()){Clear();return Latest;}
        if(Environment.TickCount64<retryAt)return Latest;
        try
        {
            handle??=Connect();var sensors=new List<SensorValue>();
            for(byte zone=1;zone<=2;zone++)
            {
                if(!permitted()){Clear();return Latest;}
                byte[] request=Request(zone);
                if(!HidD_SetFeature(handle,request,request.Length))throw new Win32Exception(Marshal.GetLastWin32Error());
                // Firmware needs time to refresh the reply. Blocking wait on the
                // existing hardware worker consumes no CPU and no UI time.
                Thread.Sleep(100);
                if(!permitted()){Clear();return Latest;}
                byte[] response=new byte[91];
                if(!HidD_GetFeature(handle,response,response.Length))throw new Win32Exception(Marshal.GetLastWin32Error());
                double rpm=Parse(response,zone);Exchanges++;
                sensors.Add(new("blade:fan:"+zone,"Chassis fan "+zone,rpm,"rpm",DateTimeOffset.UtcNow,"fresh","Blade HID actual-RPM getter"));
            }
            if(!permitted()){Clear();return Latest;}
            return Latest=new(sensors.ToArray(),null);
        }
        catch(Exception ex) when(ex is Win32Exception or InvalidDataException)
        {
            Clear();retryAt=Environment.TickCount64+60000;
            return Latest=new([],"Chassis fans unavailable: "+ex.Message);
        }
    }
    SafeFileHandle Connect()
    {
        HidD_GetHidGuid(out var guid);string? candidate=null;var lengths=new List<int>();
        foreach(string path in DeviceIo.Paths(guid))
        {
            // Blade 14 (2025) tested physical control interface; never a virtual keyboard
            // or arbitrary unknown model/firmware.
            if(!path.StartsWith(@"\\?\hid#vid_1532&pid_02c5&",StringComparison.OrdinalIgnoreCase))continue;
            using var probe=DeviceIo.Open(path,0);
            var attributes=new Attributes{Size=Marshal.SizeOf<Attributes>()};
            if(!HidD_GetAttributes(probe,ref attributes))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(attributes.Vendor!=0x1532 || attributes.Product!=0x02c5)continue;
            if(!HidD_GetPreparsedData(probe,out nint prepared))throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] caps=new byte[64];
                if(HidP_GetCaps(prepared,caps)!=0x110000)throw new InvalidDataException("Invalid Blade HID capabilities");
                lengths.Add(BitConverter.ToUInt16(caps,8));
                if(BitConverter.ToUInt16(caps,8)!=91)continue;
                if(candidate is not null)throw new InvalidDataException("Ambiguous Blade control interfaces");
                candidate=path;
            }
            finally{HidD_FreePreparsedData(prepared);}
        }
        return DeviceIo.Open(candidate??throw new InvalidDataException("Tested Blade control interface not found (feature lengths: "+string.Join(",",lengths)+")"),0);
    }
    public static byte[] Request(byte zone)
    {
        if(zone is not (1 or 2))throw new ArgumentOutOfRangeException(nameof(zone));
        byte[] result=new byte[91];result[2]=0x1f;result[6]=3;result[7]=0x0d;result[8]=0x88;result[10]=zone;
        for(int i=3;i<89;i++)result[89]^=result[i];return result;
    }
    public static double Parse(byte[] reply,byte zone)
    {
        if(zone is not (1 or 2) || reply.Length!=91 || reply[0]!=0 || reply[1]!=2 || reply[2]!=0x1f || reply[3]!=0 || reply[4]!=0
            || reply[5]!=0 || reply[6] is <3 or >80 || reply[7]!=0x0d || reply[8]!=0x88 || reply[9]!=0 || reply[10]!=zone)
            throw new InvalidDataException("Blade fan reply does not match the requested getter/zone");
        byte checksum=0;for(int i=3;i<89;i++)checksum^=reply[i];
        if(checksum!=reply[89])throw new InvalidDataException("Invalid Blade fan checksum");
        int rpm=reply[11]*100;
        if(rpm>12000)throw new InvalidDataException("Invalid Blade fan RPM");
        return rpm;
    }
    public void Clear(){handle?.Dispose();handle=null;Latest=DeviceReading.Empty;}
    public void Dispose()=>Clear();
    [StructLayout(LayoutKind.Sequential)]struct Attributes{public int Size;public ushort Vendor,Product,Version;}
    [DllImport("hid.dll")]static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll",SetLastError=true)]static extern bool HidD_GetAttributes(SafeFileHandle handle,ref Attributes attributes);
    [DllImport("hid.dll",SetLastError=true)]static extern bool HidD_GetPreparsedData(SafeFileHandle handle,out nint data);
    [DllImport("hid.dll")]static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")]static extern int HidP_GetCaps(nint data,byte[] caps);
    [DllImport("hid.dll",SetLastError=true)]static extern bool HidD_SetFeature(SafeFileHandle handle,byte[] report,int length);
    [DllImport("hid.dll",SetLastError=true)]static extern bool HidD_GetFeature(SafeFileHandle handle,byte[] report,int length);
}
