using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OWY.Core;
using OWY.Hardware;

static class EcoQoSTests
{
    [StructLayout(LayoutKind.Sequential)]
    public struct State {public uint Version,ControlMask,StateMask;}
    static State ThreadState()
    {
        var value=new State{Version=1};
        if(!GetThreadInformation(GetCurrentThread(),3,ref value,12))throw new Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }
    static State ProcessState(Process process)
    {
        var value=new State{Version=1};
        if(!GetProcessInformation(process.Handle,4,ref value,12))throw new Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }
    static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    static bool Same(State a,State b)=>a.ControlMask==b.ControlMask && a.StateMask==b.StateMask;
    static bool Enabled(State s)=>(s.ControlMask&1)==1 && (s.StateMask&1)==1;

    public static void Run()
    {
        var before=ThreadState();using var current=Process.GetCurrentProcess();var processBefore=ProcessState(current);
        using(var scope=EcoQoS.EnterThread())
        {
            var active=ThreadState();Check(Enabled(active),"Background thread enters EcoQoS");
            Check(Same(processBefore,ProcessState(current)),"Thread EcoQoS leaves process/UI policy unchanged");
            using(EcoQoS.EnterThread())Check(Enabled(ThreadState()),"Nested EcoQoS remains enabled");
            Check(Same(active,ThreadState()),"Nested scope restores outer EcoQoS");
            Exception? wrongThread=null;
            var thread=new Thread(()=>{try{scope.Dispose();}catch(InvalidOperationException ex){wrongThread=ex;}});
            thread.Start();thread.Join();Check(wrongThread is InvalidOperationException,"Cross-thread restore is rejected");
            Check(Enabled(ThreadState()),"Rejected restore leaves original thread scope intact");
        }
        Check(Same(before,ThreadState()),"Background thread restores prior policy");
        try{using(EcoQoS.EnterThread())throw new IOException("Expected test exception");}
        catch(IOException){}
        Check(Same(before,ThreadState()),"Failed work restores prior thread policy");
        var affinity=current.ProcessorAffinity;
        try
        {
            EcoQoS.EnableProcess(current);Check(Enabled(ProcessState(current)),"Background process enters EcoQoS");
            Check(current.ProcessorAffinity==affinity,"EcoQoS preserves processor affinity");
        }
        finally
        {
            if(!SetProcessInformation(current.Handle,4,ref processBefore,12))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        Check(Same(processBefore,ProcessState(current)),"Native test restores process policy");
        Console.WriteLine("Native EcoQoS scopes, failure restoration, process policy and affinity checks passed");
    }

    public static async Task Live(string[] args)
    {
        int at=Array.IndexOf(args,"--ecoqos-live");
        string package=Path.GetFullPath(args.ElementAtOrDefault(at+1)??throw new ArgumentException("Package directory required"));
        string output=args.ElementAtOrDefault(at+2)??throw new ArgumentException("Numeric report path required");
        var clock=Stopwatch.StartNew();var codexTimes=new List<double>();var hardwareTimes=new List<double>();
        long sequence=0;object gate=new();
        using var current=Process.GetCurrentProcess();var defaultBefore=ProcessState(current);
        var codex=new CodexClient(package,r=>{lock(gate){if(r.Sequence>0){codexTimes.Add(clock.Elapsed.TotalSeconds);sequence=r.Sequence;}}return ValueTask.CompletedTask;});
        var hardware=new WindowsSensors(r=>{lock(gate)hardwareTimes.Add(clock.Elapsed.TotalSeconds);return ValueTask.CompletedTask;});
        State workerState=default;int workerPid=0;bool codexApplied=false;long hardwareCycles=0;
        try
        {
            hardware.Start();codex.Start();await Task.Delay(TimeSpan.FromSeconds(30));
            Check(codex.LastIssue is null,"Live worker failed: "+codex.LastIssue);
            workerPid=codex.ProcessId;using var worker=Process.GetProcessById(workerPid);workerState=ProcessState(worker);
            codexApplied=codex.EcoQoSApplied;hardwareCycles=hardware.EcoQoSCycles;
            Check(codexApplied && Enabled(workerState),"Running Codex worker has EcoQoS enabled");
            Check(hardwareCycles>=25,"Hardware collector completed EcoQoS cycles");
            Check(Same(defaultBefore,ProcessState(current)),"Collector leaves parent process QoS unchanged");
        }
        finally{await Task.WhenAll(codex.DisposeAsync().AsTask(),hardware.DisposeAsync().AsTask());}
        double[] c,h;lock(gate){c=codexTimes.ToArray();h=hardwareTimes.ToArray();}
        static object Cadence(double[] times)
        {
            var gaps=times.Zip(times.Skip(1),(a,b)=>b-a).ToArray();
            Check(times.Length>=25,"One-second collector emitted enough frames");
            Check(gaps.Max()<3,"Collector exceeded live freshness limit");
            var sorted=gaps.Order().ToArray();
            return new{frames=times.Length,first_at_seconds=times[0],mean_gap_seconds=gaps.Average(),median_gap_seconds=sorted[sorted.Length/2],max_gap_seconds=gaps.Max()};
        }
        var report=new{version=1,time=DateTimeOffset.UtcNow,seconds=clock.Elapsed.TotalSeconds,codex=Cadence(c),hardware=Cadence(h),worker_sequence=sequence,
            worker_pid=workerPid,worker_ecoqos_applied=codexApplied,worker_control_mask=workerState.ControlMask,worker_state_mask=workerState.StateMask,
            hardware_ecoqos_cycles=hardwareCycles,parent_control_mask=defaultBefore.ControlMask,parent_state_mask=defaultBefore.StateMask,worker_stopped=codex.ProcessId==0,
            devices=hardware.DeviceDiagnostics};
        File.WriteAllText(output,JsonSerializer.Serialize(report,Protocol.Json));
        Console.WriteLine(JsonSerializer.Serialize(report,Protocol.Json));
    }
    [DllImport("kernel32.dll")]static extern nint GetCurrentThread();
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetThreadInformation(nint thread,int kind,ref State value,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessInformation(nint process,int kind,ref State value,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetProcessInformation(nint process,int kind,ref State value,uint size);
}
