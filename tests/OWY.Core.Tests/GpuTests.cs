using System.Diagnostics;
using System.Text.Json;
using OWY.Core;
using OWY.Hardware;

static class GpuTests
{
    static void Check(bool condition,string scenario){if(!condition)throw new InvalidOperationException(scenario);}
    public static Task Run()
    {
        GpuAdapter[] devices=[new("amd","AMD Radeon 880M","integrated"),new("nv","NVIDIA GeForce RTX 5070 Laptop GPU","discrete")];
        var idle=GpuSelection.Select(devices,new Dictionary<string,double>{{"amd",5},{"nv",0}},new HashSet<string>{"amd"});
        Check(idle.Kind=="integrated" && !idle.AllowNvidiaSensors && idle.Percent==5,"Idle dGPU selects the iGPU without requesting NVIDIA sensors");
        var busy=GpuSelection.Select(devices,new Dictionary<string,double>{{"amd",5},{"nv",20}},new HashSet<string>{"amd"});
        Check(busy.Kind=="discrete" && busy.AllowNvidiaSensors && busy.Percent==20,"NVIDIA engine activity selects the dGPU");
        var display=GpuSelection.Select(devices,new Dictionary<string,double>{{"nv",0}},new HashSet<string>{"nv"});
        Check(display.AllowNvidiaSensors && display.Basis=="Windows active display","NVIDIA scanout remains active without 3D work");
        Check(!GpuSelection.Select(devices,null,new HashSet<string>()).AllowNvidiaSensors,"Missing activity fails closed");
        Check(!GpuSelection.Select(devices,new Dictionary<string,double>{{"nv",double.NaN}},new HashSet<string>()).AllowNvidiaSensors,"Invalid activity cannot wake NVIDIA");
        Check(GpuSelection.Select(devices,new Dictionary<string,double>(),new HashSet<string>()).Percent is null,"Missing iGPU counters are not reported as zero");
        Check(!GpuSelection.Select([devices[1]],new Dictionary<string,double>{{"nv",0}},new HashSet<string>()).AllowNvidiaSensors,"A present NVIDIA-only device is not presumed active");
        Check(GpuSelection.Select([devices[1]],null,new HashSet<string>{"nv"}).AllowNvidiaSensors,"NVIDIA-only active display permits measurement");
        var duplicate=new GpuAdapter("nv2","NVIDIA second GPU","discrete");
        Check(!GpuSelection.Select([..devices,duplicate],new Dictionary<string,double>{{"nv",3},{"nv2",2}},new HashSet<string>()).AllowNvidiaSensors,"Ambiguous multiple boards fail closed");
        var at=DateTimeOffset.UtcNow;
        var amd=new AmdReading("AMD Radeon 880M",14,3,[],null);
        var nvidia=new NvidiaReading(devices[1].Name,[new("nvidia:board:power","NVIDIA board",8,"W",at,"fresh","fixture")],null,at);
        Check(GpuSelection.Power(idle,amd,nvidia)==(14d,"APU"),"Integrated mode excludes both NVIDIA and overlapping GFX power");
        Check(GpuSelection.Power(busy,amd,nvidia)==(22d,"APU+dGPU"),"Discrete mode adds the board once and never adds CPU/GFX to APU again");
        Check(GpuSelection.Power(idle,amd with{PackageWatts=null,GraphicsWatts=0},nvidia)==(0d,"iGPU"),"A valid zero graphics wattage stays available");
        Check(GpuSelection.Power(idle,amd with{Issue="driver failed"},nvidia).Watts is null,"AMD errors never reuse an old package value");
        Check(GpuSelection.Power(busy,amd,nvidia.Expire(at.AddSeconds(16),TimeSpan.FromSeconds(15)))==(14d,"APU"),"Missing board power retains an accurately labelled package subtotal");
        Check(GpuSelection.Power(idle,amd with{PackageWatts=double.NaN,GraphicsWatts=-1},nvidia).Watts is null,"Invalid AMD power is excluded");
        Check(GpuSelection.Power(busy,amd,nvidia with{Sensors=[..nvidia.Sensors,nvidia.Sensors[0]]})==(14d,"APU"),"Duplicate boards are not added");
        Check(busy.SelectedPower(amd,nvidia with{Sensors=[..nvidia.Sensors,nvidia.Sensors[0]]}) is null,"Ambiguous boards are not shown as selected GPU watts");
        Check(idle.SelectedPower(amd with{GraphicsWatts=double.NaN},nvidia) is null,"Invalid selected GPU watts remain unavailable");
        Check(GpuSelection.Power(busy,amd with{PackageWatts=null},nvidia)==(8d,"GPU"),"Missing APU never promotes a GPU subtotal to whole-system watts");
        Console.WriteLine("GPU auto selection, non-waking guard and power-domain regressions passed");
        return Task.CompletedTask;
    }
    public static async Task Live()
    {
        var samples=new List<object>();long sequence=0;
        await using var sensors=new WindowsSensors(value=>
        {
            samples.Add(new{sequence=++sequence,value.SampledAt,value.ComponentWatts,value.ComponentPowerBasis,value.ActiveGpu,value.ActiveGpuKind,value.ActiveGpuWatts,value.GpuSelectionBasis,value.Issues});
            return ValueTask.CompletedTask;
        },true);
        sensors.EnableTaskbarPower(true);sensors.Start();
        using var process=Process.GetCurrentProcess();double before=process.TotalProcessorTime.TotalSeconds;var elapsed=Stopwatch.StartNew();
        await Task.Delay(20000);sensors.EnableDetails(true);await Task.Delay(10000);
        sensors.EnableTaskbarPower(false);sensors.EnableDetails(false);await Task.Delay(6000);
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new{samples,selection=sensors.GpuSelection,adapter_loads=sensors.GpuAdapterLoads,amd=sensors.AmdReading,devices=sensors.DeviceDiagnostics,
            seconds=elapsed.Elapsed.TotalSeconds,one_core_percent=100*(process.TotalProcessorTime.TotalSeconds-before)/elapsed.Elapsed.TotalSeconds,cpu_milliseconds=sensors.CpuMilliseconds},Protocol.Json));
    }
}
