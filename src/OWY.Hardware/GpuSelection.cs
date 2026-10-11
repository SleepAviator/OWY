using System.Runtime.InteropServices;
using OWY.Core;

namespace OWY.Hardware;

public sealed record GpuAdapter(string Luid,string Name,string Kind);
public sealed record GpuSelection(string Kind,string Name,double? Percent,bool AllowNvidiaSensors,string Basis)
{
    public static GpuSelection Unknown=>new("unavailable","Unavailable",null,false,"GPU activity unavailable");
    static double? ValidWatts(double? value)=>value is {} n && double.IsFinite(n) && n is >=0 and <=1000?n:null;
    public double? SelectedPower(AmdReading amd,NvidiaReading nvidia)=>Kind=="integrated" && amd.Issue is null?ValidWatts(amd.GraphicsWatts):
        AllowNvidiaSensors?nvidia.BoardWatts:null;
    // Only positive OS evidence permits hardware polling. An idle counter does
    // not prove physical sleep; it simply gives us no reason to poll the dGPU.
    public static GpuSelection Select(GpuAdapter[] adapters,IReadOnlyDictionary<string,double>? activity,IReadOnlySet<string> displays)
    {
        double? Load(string luid)=>activity is not null && activity.TryGetValue(luid,out double value) && double.IsFinite(value) && value is >=0 and <=100?value:null;
        var active=adapters.Where(a=>a.Kind=="discrete" && a.Name.Contains("NVIDIA",StringComparison.OrdinalIgnoreCase)
            && (displays.Contains(a.Luid) || Load(a.Luid)>0)).ToArray();
        if(active.Length==1)
        {
            var gpu=active[0];return new("discrete",gpu.Name,Load(gpu.Luid),true,
                displays.Contains(gpu.Luid)?"Windows active display":"Windows GPU engine activity");
        }
        // Multiple discrete devices cannot safely share a single board reading.
        if(active.Length>1)return Unknown with{Basis="Multiple active discrete GPUs"};
        var integrated=adapters.Where(a=>a.Kind=="integrated").ToArray();
        return integrated.Length==1?new("integrated",integrated[0].Name,Load(integrated[0].Luid),false,
            activity is null?"dGPU activity unavailable; NVIDIA polling skipped":"No dGPU activity observed"):Unknown;
    }
    public static (double? Watts,string Basis) Power(GpuSelection selected,AmdReading amd,NvidiaReading nvidia)
    {
        double? package=amd.Issue is null?ValidWatts(amd.PackageWatts):null;
        double? graphics=amd.Issue is null?ValidWatts(amd.GraphicsWatts):null;
        if(!selected.AllowNvidiaSensors)
            return package.HasValue?(package,"APU"):graphics.HasValue?(graphics,"iGPU"):(null,"unavailable");
        double? discrete=nvidia.BoardWatts;
        // The AMD ASIC is the APU package. Do not add its CPU or GFX subdomain.
        if(package.HasValue)return discrete.HasValue?(package+discrete,"APU+dGPU"):(package,"APU");
        return discrete.HasValue?(discrete,"GPU"):(null,"unavailable");
    }
}

static class GpuTopology
{
    // These queries read Windows adapter metadata, not temperatures, clocks,
    // power or vendor performance interfaces. Never create a GPU device here.
    public static GpuAdapter[] Adapters()
    {
        int bytes=Marshal.SizeOf<AdapterInfo>();nint buffer=Marshal.AllocHGlobal(bytes*16);
        var request=new EnumAdapters{Count=16,Adapters=buffer};
        try
        {
            int status=D3DKMTEnumAdapters2(ref request);
            if(status!=0 || request.Count>16)throw new InvalidDataException("Windows GPU enumeration failed (0x"+status.ToString("x")+")");
            var result=new List<GpuAdapter>();
            for(int i=0;i<request.Count;i++)
            {
                var adapter=Marshal.PtrToStructure<AdapterInfo>(buffer+i*bytes);
                try
                {
                    nint data=Marshal.AllocHGlobal(2080);
                    try
                    {
                        var info=new QueryInfo{Adapter=adapter.Handle,Type=8,Data=data,Size=2080};
                        if(D3DKMTQueryAdapterInfo(ref info)!=0)continue; // Removed or software adapter.
                        string name=Marshal.PtrToStringUni(data,260)?.Split('\0')[0]??"";
                        info.Type=15;info.Size=4;
                        if(D3DKMTQueryAdapterInfo(ref info)!=0)continue;
                        uint flags=(uint)Marshal.ReadInt32(data);
                        if((flags&4)!=0)continue; // Software device.
                        string kind=(flags&32)!=0?"integrated":(flags&16)!=0?"discrete":"unknown";
                        // NVIDIA-only mode can omit the hybrid bit. The known
                        // NVIDIA adapter is still discrete, without assuming it is on.
                        if(kind=="unknown" && name.Contains("NVIDIA",StringComparison.OrdinalIgnoreCase))kind="discrete";
                        result.Add(new(adapter.Luid.Key,name,kind));
                    }
                    finally{Marshal.FreeHGlobal(data);}
                }
                finally{var close=new CloseAdapter{Adapter=adapter.Handle};if(D3DKMTCloseAdapter(ref close)!=0)throw new InvalidDataException("Windows GPU metadata handle could not be closed");}
            }
            return result.ToArray();
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    public static HashSet<string> ActiveDisplays()
    {
        // QDC_ONLY_ACTIVE_PATHS. RDP/headless sessions may deny access; they
        // require engine activity instead and never permit a blind dGPU read.
        for(int attempt=0;attempt<2;attempt++)
        {
            if(GetDisplayConfigBufferSizes(2,out uint paths,out uint modes)!=0)return [];
            if(paths>64 || modes>256)return [];
            nint pathBuffer=Marshal.AllocHGlobal(checked((int)paths*72));
            nint modeBuffer=Marshal.AllocHGlobal(checked((int)modes*64));
            try
            {
                int status=QueryDisplayConfig(2,ref paths,pathBuffer,ref modes,modeBuffer,0);
                if(status==122)continue;
                if(status!=0 || paths>64)return [];
                var result=new HashSet<string>(StringComparer.Ordinal);
                for(int i=0;i<paths;i++)
                {
                    // Source and target may belong to different adapters.
                    result.Add(Marshal.PtrToStructure<Luid>(pathBuffer+i*72).Key);
                    result.Add(Marshal.PtrToStructure<Luid>(pathBuffer+i*72+20).Key);
                }
                return result;
            }
            finally{Marshal.FreeHGlobal(pathBuffer);Marshal.FreeHGlobal(modeBuffer);}
        }
        return [];
    }
    [StructLayout(LayoutKind.Sequential)]struct Luid{public uint Low;public int High;public readonly string Key=>$"luid_0x{(uint)High:x8}_0x{Low:x8}";}
    [StructLayout(LayoutKind.Sequential)]struct AdapterInfo{public uint Handle;public Luid Luid;public uint Sources;public int PrecisePresent;}
    [StructLayout(LayoutKind.Sequential)]struct EnumAdapters{public uint Count;public nint Adapters;}
    [StructLayout(LayoutKind.Sequential)]struct QueryInfo{public uint Adapter,Type;public nint Data;public uint Size;}
    [StructLayout(LayoutKind.Sequential)]struct CloseAdapter{public uint Adapter;}
    [DllImport("gdi32.dll")]static extern int D3DKMTEnumAdapters2(ref EnumAdapters value);
    [DllImport("gdi32.dll")]static extern int D3DKMTQueryAdapterInfo(ref QueryInfo value);
    [DllImport("gdi32.dll")]static extern int D3DKMTCloseAdapter(ref CloseAdapter value);
    [DllImport("user32.dll")]static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")]static extern int QueryDisplayConfig(uint flags,ref uint paths,nint pathArray,ref uint modes,nint modeArray,nint topology);
}
