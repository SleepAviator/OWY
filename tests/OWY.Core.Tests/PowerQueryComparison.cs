using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OWY.Core;
using OWY.Hardware;

// A benchmark, not a production collector. The same low-rate AMD watt observer
// stays active in every block, including idle. Only OWY's real collector toggles.
static class PowerQueryComparison
{
    public static async Task Run(string[] args)
    {
        string Option(string name,string fallback)
        {
            int at=Array.IndexOf(args,name);
            return at<0?fallback:args.ElementAtOrDefault(at+1)??throw new ArgumentException(name+" needs a value");
        }
        int seconds=int.Parse(Option("--block-seconds","90"));
        int settle=int.Parse(Option("--settle-seconds","20"));
        int repeats=int.Parse(Option("--blocks","8"));
        if(seconds is <10 or >300 || settle is <5 or >60 || repeats is not (2 or 8))
            throw new ArgumentException("Use 10–300 second blocks, 5–60 second settling, and 2 or 8 blocks");
        string path=Path.GetFullPath(Option("--output","artifacts/power/query-vs-idle.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var samples=new List<object>();var blocks=new List<object>();
        long callbacks=0;
        await using var collector=new WindowsSensors(_=>{Interlocked.Increment(ref callbacks);return ValueTask.CompletedTask;});
        collector.EnableTaskbarPower(true);collector.Suspend(true);collector.Start();
        using var observer=new AmdWattObserver();
        using var process=Process.GetCurrentProcess();
        bool[] order=repeats==2?[false,true]:[true,false,false,true,false,true,true,false];
        var runClock=Stopwatch.StartNew();
        string? failure=null;
        try
        {
            // Warm driver/JIT work outside the measurement; preserve the same
            // observer and cadence after warmup and across every transition.
            for(int i=0;i<15;i++)
            {
                if(!observer.Read().PackageWatts.HasValue)throw new InvalidDataException("AMD package watt counter unavailable");
                await Task.Delay(1000);
            }
            for(int block=0;block<order.Length;block++)
            {
                bool querying=order[block];collector.Suspend(!querying);
                var settling=Stopwatch.StartNew();
                while(settling.Elapsed.TotalSeconds<settle){observer.Read();await Task.Delay(1000);}
                process.Refresh();double cpuBefore=process.TotalProcessorTime.TotalSeconds;
                ulong[] systemBefore=AmdWattObserver.Cpu();
                var clock=Stopwatch.StartNew();
                long callbacksBefore=Interlocked.Read(ref callbacks);
                var powers=new List<double>();var cores=new List<double>();var loads=new List<double>();
                var clocks=new List<double>();var gpuPowers=new List<double>();
                long oldBucket=-1;int changes=0;double? lastPower=null;
                while(clock.Elapsed.TotalSeconds<seconds)
                {
                    await Task.Delay(1000);
                    var value=observer.Read();var sys=AmdWattObserver.Cpu();
                    double elapsed=clock.Elapsed.TotalSeconds;
                    double? load=sys[1]>systemBefore[1]?100*(1-(sys[0]-systemBefore[0])/(double)(sys[1]-systemBefore[1])):null;
                    if(load.HasValue)loads.Add(load.Value);
                    if(value.PackageWatts.HasValue){powers.Add(value.PackageWatts.Value);if(lastPower.HasValue && lastPower!=value.PackageWatts)changes++;lastPower=value.PackageWatts;}
                    if(value.CpuDomainWatts.HasValue)cores.Add(value.CpuDomainWatts.Value);
                    if(value.CpuClockMHz.HasValue)clocks.Add(value.CpuClockMHz.Value);
                    if(value.GraphicsWatts.HasValue)gpuPowers.Add(value.GraphicsWatts.Value);
                    samples.Add(new{at=DateTimeOffset.UtcNow,block,querying,elapsed_seconds=elapsed,
                        watts=value,system_cpu_percent=load,collector_gpu=collector.GpuSelection.Kind,
                        nvidia=collector.NvidiaDiagnostics,devices=collector.DeviceDiagnostics});
                    long bucket=(long)elapsed/10;
                    if(bucket!=oldBucket)
                    {
                        oldBucket=bucket;
                        Console.WriteLine(JsonSerializer.Serialize(new{kind="progress",block,querying,elapsed_seconds=elapsed,
                            remaining_seconds=Math.Max(0,seconds-elapsed),package_watts=value.PackageWatts,cpu_domain_watts=value.CpuDomainWatts},Protocol.Json));
                    }
                }
                process.Refresh();double duration=clock.Elapsed.TotalSeconds;
                if(Interlocked.Read(ref callbacks)-callbacksBefore<seconds/2)
                    throw new InvalidDataException("Production collector did not keep publishing");
                if(querying && !collector.AmdReading.PackageWatts.HasValue)
                    throw new InvalidDataException("Production AMD polling did not yield watts");
                var summary=new{block,querying,seconds=duration,samples=powers.Count,
                    mean_apu_watts=powers.Count>0?(double?)powers.Average():null,
                    min_apu_watts=powers.Count>0?(double?)powers.Min():null,
                    max_apu_watts=powers.Count>0?(double?)powers.Max():null,
                    apu_changes=changes,mean_cpu_domain_watts=cores.Count>0?(double?)cores.Average():null,
                    mean_cpu_clock_mhz=clocks.Count>0?(double?)clocks.Average():null,
                    mean_igpu_watts=gpuPowers.Count>0?(double?)gpuPowers.Average():null,
                    system_cpu_percent=loads.Count>0?(double?)loads[^1]:null,
                    benchmark_one_core_percent=100*(process.TotalProcessorTime.TotalSeconds-cpuBefore)/duration,
                    collector_callbacks=Interlocked.Read(ref callbacks)-callbacksBefore,
                    nvidia=collector.NvidiaDiagnostics,devices=collector.DeviceDiagnostics,
                    collector_issue=collector.Latest.Issues};
                blocks.Add(summary);Console.WriteLine(JsonSerializer.Serialize(new{kind="block",summary},Protocol.Json));
            }
        }
        catch(Exception ex)
        {
            failure=ex.GetType().Name+": "+ex.Message;throw;
        }
        finally
        {
            collector.Suspend(true);
            var result=new{version=1,at=DateTimeOffset.UtcNow,scope="Production WindowsSensors querying versus suspended; OWY UI/worker unchanged; AMD observer active in both phases",
                observer_cadence_seconds=1,settle_seconds=settle,block_target_seconds=seconds,
                seconds=runClock.Elapsed.TotalSeconds,observer_adapter=observer.Name,completed=blocks.Count==repeats && failure is null,failure,blocks,samples,
                limitations="APU package includes CPU and integrated GPU, not whole-system watts. Integer driver watts and background activity limit precision. CPU-domain zero is not proof of zero CPU consumption. The observer itself queries AMD at the same cadence in every phase. This does not measure visible UI, battery life, or physical GPU sleep."};
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(result,Protocol.Json));
            Console.WriteLine(JsonSerializer.Serialize(new{kind="saved",path,failure,blocks=blocks.Count},Protocol.Json));
        }
    }
}

// Read only the installed AMD driver. IDs are from AMD's public adl_defines.h:
// Reuse the production ADL reader so benchmark and UI decode the same domains.
sealed class AmdWattObserver : IDisposable
{
    public sealed record Reading(double? PackageWatts,double? CpuDomainWatts,double? GraphicsWatts,double? CpuClockMHz,double? CpuTemperatureC,double? GraphicsLoadPercent);
    readonly AmdSensors sensors=new();
    public string Name=>sensors.Latest.Name;
    public Reading Read()
    {
        var value=sensors.Sample(DateTimeOffset.UtcNow);
        if(value.Issue is {} issue)throw new InvalidDataException(issue);
        double? Get(string id)=>value.Sensors.SingleOrDefault(s=>s.Id==id)?.Value;
        return new(value.PackageWatts,Get("amd:cpu:power"),value.GraphicsWatts,Get("amd:cpu:clock"),Get("amd:cpu:temperature"),Get("amd:igpu:load"));
    }
    public static ulong[] Cpu()
    {
        if(!GetSystemTimes(out ulong idle,out ulong kernel,out ulong user))throw new System.ComponentModel.Win32Exception();
        return [idle,kernel+user];
    }
    public void Dispose()=>sensors.Dispose();
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
}
