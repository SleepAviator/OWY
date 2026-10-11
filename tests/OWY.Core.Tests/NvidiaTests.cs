using System.Diagnostics;
using System.Text.Json;
using OWY.Core;
using OWY.Hardware;

static class NvidiaTests
{
    static int checks;
    static void Check(bool condition,string scenario){checks++;if(!condition)throw new InvalidOperationException(scenario);}
    static readonly GpuSelection Active=new("discrete","NVIDIA GeForce RTX 5070 Laptop GPU",10,true,"fixture Windows activity");
    static NvidiaReading Reading(DateTimeOffset at)=>new(Active.Name,
        [new("nvidia:board:power","NVIDIA board power",8.5,"W",at,"fresh","fixture")],null,at);
    sealed class FakeApi : INvidiaApi
    {
        public int Opens,Reads,Closes;public string? ExpectedName;
        public Action? OnOpen,OnRead,OnClose;
        public void Open(string name){Opens++;ExpectedName=name;OnOpen?.Invoke();}
        public NvidiaReading Read(DateTimeOffset now){Reads++;OnRead?.Invoke();return Reading(now);}
        public void Close(){Closes++;OnClose?.Invoke();}
    }
    public static void Run()
    {
        var at=DateTimeOffset.UtcNow;bool allowed=true;var api=new FakeApi();
        using(var reader=new NvidiaSensors(()=>allowed,api))
        {
            reader.Sample(Active with{AllowNvidiaSensors=false},at);
            allowed=false;reader.Sample(Active,at);
            Check(api.Opens==0 && api.Reads==0 && api.Closes==0,"Denied activity performs no NVIDIA driver I/O");
            allowed=true;reader.Sample(Active,at);reader.Sample(Active,at.AddSeconds(5));
            Check(api.Opens==1 && api.Reads==2 && reader.Initialized,"Active samples reuse the driver context");
            Check(api.ExpectedName==Active.Name && reader.Latest.BoardWatts==8.5,"Windows device identity and board watts reach the driver boundary");
            allowed=false;reader.Sample(Active,at.AddSeconds(6));reader.Clear();
            Check(api.Closes==1 && !reader.Initialized && reader.Latest.Sensors.Length==0,"Permission loss releases once and removes old values");
            allowed=true;reader.Sample(Active,at.AddSeconds(7));
            reader.Sample(Active with{Name="Another NVIDIA board"},at.AddSeconds(12));
            Check(api.Opens==3 && api.Closes==2 && api.ExpectedName=="Another NVIDIA board","Device changes require a new identified context");
        }
        Check(api.Closes==3,"Clean disposal releases the remaining context");
        allowed=true;api=new(){OnOpen=()=>allowed=false};
        using(var reader=new NvidiaSensors(()=>allowed,api))
        {
            reader.Sample(Active,at);
            Check(api.Opens==1 && api.Reads==0 && api.Closes==1 && !reader.Initialized,"Permission withdrawn during initialization prevents querying");
        }
        allowed=true;api=new(){OnRead=()=>allowed=false};
        using(var reader=new NvidiaSensors(()=>allowed,api))
        {
            reader.Sample(Active,at);
            Check(reader.Samples==0 && reader.Latest.Sensors.Length==0 && api.Closes==1,"Permission withdrawn during a query discards its result");
        }
        api=new(){OnOpen=()=>throw new InvalidDataException("Fixture identity mismatch")};
        using(var reader=new NvidiaSensors(()=>true,api))
        {
            reader.Sample(Active,at);reader.Sample(Active,at.AddSeconds(5));
            Check(reader.Latest.Issue=="Fixture identity mismatch" && api.Opens==1 && api.Closes==1,"Failed partial initialization cleans up and backs off retries");
            reader.Clear();reader.Sample(Active,at);
            Check(api.Opens==2,"A new active period can retry a failed initialization");
        }
        api=new(){OnRead=()=>throw new EntryPointNotFoundException()};
        using(var reader=new NvidiaSensors(()=>true,api))
        {
            reader.Sample(Active,at);
            Check(reader.Latest.Issue!.Contains("EntryPointNotFoundException") && api.Closes==1 && !reader.Initialized,"Unsupported driver API failures are explicit and release the context");
        }
        api=new(){OnClose=()=>throw new InvalidDataException("Fixture shutdown failure")};
        using(var reader=new NvidiaSensors(()=>true,api))
        {
            reader.Sample(Active,at);reader.Clear();
            Check(reader.Latest.Issue=="Fixture shutdown failure" && !reader.Initialized,"Shutdown errors are visible rather than hidden");
        }
        var reading=Reading(at);var sensor=reading.Sensors[0];
        Check(reading.Expire(at.AddSeconds(15),TimeSpan.FromSeconds(15)).BoardWatts==8.5,"Fresh sample boundary retains the reading");
        Check(reading.Expire(at.AddSeconds(16),TimeSpan.FromSeconds(15)).BoardWatts is null,"Expired readings cannot supply current watts");
        Check(reading.Expire(at.AddSeconds(-1),TimeSpan.FromSeconds(15)).BoardWatts is null,"Future-dated readings fail freshness");
        Check((reading with{Sensors=[sensor,sensor]}).BoardWatts is null,"Duplicate board readings remain unavailable");
        foreach(double value in new[]{double.NaN,double.PositiveInfinity,-1,1001})
            Check((reading with{Sensors=[sensor with{Value=value}]}).BoardWatts is null,"Invalid board power cannot enter the subtotal");
        Check((reading with{Sensors=[sensor with{Value=0}]}).BoardWatts==0,"Measured zero remains available");
        Check((reading with{Sensors=[sensor with{Unit="%"}]}).BoardWatts is null,"Relative GPU power is never interpreted as watts");
        Check((reading with{Sensors=[sensor with{Status="unsupported"}]}).BoardWatts is null,"Unsupported readings cannot enter the subtotal");
        Console.WriteLine($"{checks} direct NVIDIA permission, resource cleanup and power/freshness checks passed");
    }
    public static async Task Live()
    {
        var samples=new List<object>();
        var sensors=new WindowsSensors(value=>
        {
            samples.Add(new{value.SampledAt,value.ComponentWatts,value.ComponentPowerBasis,value.ActiveGpu,value.ActiveGpuKind,value.ActiveGpuWatts,
                nvidia=value.Details.Where(s=>s.Id.StartsWith("nvidia:")).ToArray(),value.Issues});
            return ValueTask.CompletedTask;
        },true);
        // Numeric reports only. This bounded mode never enables Synapse or forces GPU work.
        using var process=Process.GetCurrentProcess();double before=process.TotalProcessorTime.TotalSeconds;var elapsed=Stopwatch.StartNew();
        object? beforePause=null;bool pauseReleased=false;
        try
        {
            sensors.EnableTaskbarPower(true);sensors.EnableDetails(true);sensors.Start();
            await Task.Delay(22000);beforePause=sensors.NvidiaDiagnostics;
            sensors.Suspend(true);await Task.Delay(2500);
            pauseReleased=sensors.NvidiaReading.Sensors.Length==0;
        }
        finally{await sensors.DisposeAsync();}
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new{samples,before_pause=beforePause,pause_released=pauseReleased,
            after_shutdown=sensors.NvidiaDiagnostics,devices=sensors.DeviceDiagnostics,
            seconds=elapsed.Elapsed.TotalSeconds,one_core_percent=100*(process.TotalProcessorTime.TotalSeconds-before)/elapsed.Elapsed.TotalSeconds,
            hardware_ecoqos_cycles=sensors.EcoQoSCycles,cpu_milliseconds=sensors.CpuMilliseconds},Protocol.Json));
    }
}
