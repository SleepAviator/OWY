using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OWY.Core;
using OWY.Hardware;

// Explicit diagnostic only, excluded from the application package. A small
// eight-second paced GPU-memory workload validates the genuinely-active case.
static class NvidiaExtraLive
{
    static void Require(int code,string name){if(code!=0)throw new InvalidDataException("CUDA validation "+name+" failed ("+code+")");}
    public static async Task Run(string[] args)
    {
        int at=Array.IndexOf(args,"--output");string path=at>=0?args[at+1]:"artifacts/validation/nvidia-extra-live.json";
        var ready=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled=new CancellationTokenSource();
        var work=Task.Run(()=>
        {
            using(EcoQoS.EnterThread())
            {
                nint context=0;ulong memory=0;
                try
                {
                    Require(cuInit(0),"initialization");Require(cuDeviceGetCount(out int count),"count");
                    if(count!=1)throw new InvalidDataException("CUDA validation requires one NVIDIA GPU");
                    var name=new StringBuilder(100);Require(cuDeviceGetName(name,100,0),"name");
                    Require(cuCtxCreate_v2(out context,4,0),"context");Require(cuMemAlloc_v2(out memory,16*1024*1024),"allocation");
                    string label=name.ToString();ready.SetResult(label.StartsWith("NVIDIA ")?label:"NVIDIA "+label);
                    var clock=Stopwatch.StartNew();
                    while(!cancelled.IsCancellationRequested && clock.Elapsed.TotalSeconds<8)
                    {Require(cuMemsetD8_v2(memory,0,16*1024*1024),"paced memory work");Thread.Sleep(200);}
                }
                catch(Exception ex){ready.TrySetException(ex);throw;}
                finally
                {
                    try{if(memory!=0)Require(cuMemFree_v2(memory),"memory release");}
                    finally{if(context!=0)Require(cuCtxDestroy_v2(context),"context release");}
                }
            }
        });
        var samples=new List<object>();using var sensors=new NvidiaExtraSensors(()=>ready.Task.IsCompletedSuccessfully && !work.IsCompleted);
        try
        {
            string name=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var selected=new GpuSelection("discrete",name,1,true,"Explicit eight-second CUDA validation workload");
            for(int i=0;i<3;i++)
            {
                await Task.Delay(1000);var clock=Stopwatch.StartNew();
                var value=sensors.Sample(selected,DateTimeOffset.UtcNow);
                samples.Add(new{value,query_ms=clock.Elapsed.TotalMilliseconds});
                if(value.Issue is {} issue)throw new InvalidDataException(issue);
                if(value.Sensors.Any(s=>s.Value is null))throw new InvalidDataException("NVIDIA extra sensor failed to yield a real value");
            }
        }
        finally{cancelled.Cancel();await work;sensors.Clear();}
        var result=new{version=1,at=DateTimeOffset.UtcNow,samples,queries=sensors.Queries,connections=sensors.Connections,releases=sensors.Releases,
            context_closed=!sensors.Initialized,workload="16 MiB device memory fill, paced at 200 ms, at most 8 s",synapse_requests=0};
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);File.WriteAllText(path,JsonSerializer.Serialize(result,Protocol.Json));
        Console.WriteLine(JsonSerializer.Serialize(result,Protocol.Json));
    }
    const string Driver="nvcuda.dll";
    [DllImport(Driver)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int cuInit(uint flags);
    [DllImport(Driver)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int cuDeviceGetCount(out int count);
    [DllImport(Driver,CharSet=CharSet.Ansi)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int cuDeviceGetName(StringBuilder name,int length,int device);
    [DllImport(Driver)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int cuCtxCreate_v2(out nint context,uint flags,int device);
    [DllImport(Driver)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int cuCtxDestroy_v2(nint context);
    [DllImport(Driver)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int cuMemAlloc_v2(out ulong memory,nuint bytes);
    [DllImport(Driver)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int cuMemsetD8_v2(ulong memory,byte value,nuint bytes);
    [DllImport(Driver)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern int cuMemFree_v2(ulong memory);
}
