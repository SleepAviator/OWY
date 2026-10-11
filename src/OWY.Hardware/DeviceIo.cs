using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OWY.Core;

namespace OWY.Hardware;

public sealed record DeviceReading(SensorValue[] Sensors,string? Issue)
{
    public static DeviceReading Empty=>new([],null);
}

// Shared Windows device discovery for battery, disk and HID readers. Paths stay
// inside the collector; snapshots contain numeric values and logical labels.
static class DeviceIo
{
    public static IEnumerable<string> Paths(Guid kind)
    {
        nint set=SetupDiGetClassDevs(ref kind,null,0,0x12);
        if(set==-1)throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for(uint i=0;i<64;i++)
            {
                var item=new InterfaceData{Size=Marshal.SizeOf<InterfaceData>()};
                if(!SetupDiEnumDeviceInterfaces(set,0,ref kind,i,ref item))
                {
                    int error=Marshal.GetLastWin32Error();
                    if(error==259)yield break;
                    throw new Win32Exception(error);
                }
                SetupDiGetDeviceInterfaceDetail(set,ref item,0,0,out uint size,0);
                if(size is <8 or >65536)throw new InvalidDataException("Invalid Windows device interface size");
                nint buffer=Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buffer,IntPtr.Size==8?8:6);
                    if(!SetupDiGetDeviceInterfaceDetail(set,ref item,buffer,size,out _,0))throw new Win32Exception(Marshal.GetLastWin32Error());
                    yield return Marshal.PtrToStringUni(buffer+4)??throw new InvalidDataException("Empty Windows device interface");
                }
                finally{Marshal.FreeHGlobal(buffer);}
            }
            throw new InvalidDataException("Too many Windows device interfaces");
        }
        finally{SetupDiDestroyDeviceInfoList(set);}
    }
    public static SafeFileHandle Open(string path,uint access)
    {
        var handle=CreateFile(path,access,3,0,3,0,0);
        if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}
        return handle;
    }
    public static byte[] Query(SafeFileHandle handle,uint code,byte[] input,int length)
    {
        byte[] output=new byte[length];
        if(!DeviceIoControl(handle,code,input,input.Length,output,length,out int returned,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(returned<0 || returned>length)throw new InvalidDataException("Invalid device reply length");
        return output[..returned];
    }
    [StructLayout(LayoutKind.Sequential)]struct InterfaceData{public int Size;public Guid Kind;public uint Flags;public nuint Reserved;}
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern nint SetupDiGetClassDevs(ref Guid kind,string? enumerator,nint parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)]static extern bool SetupDiEnumDeviceInterfaces(nint set,nint device,ref Guid kind,uint index,ref InterfaceData data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiGetDeviceInterfaceDetail(nint set,ref InterfaceData data,nint detail,uint size,out uint needed,nint device);
    [DllImport("setupapi.dll")]static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateFileW",SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint sharing,nint security,uint creation,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputLength,byte[] output,int outputLength,out int returned,nint overlapped);
}
