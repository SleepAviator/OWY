using System.Text;
using OWY.Core;
using OWY.Hardware;

if(args.Contains("--direct-sensors-live")){await DirectSensorTests.Live(args);return;}
if(args.Contains("--direct-collector-live")){await DirectSensorTests.CollectorLive(args);return;}
if(args.Contains("--nvidia-extra-active-check")){await NvidiaExtraLive.Run(args);return;}
if(args.Contains("--direct-collapsed-check")){await DirectSensorTests.CollapsedLive();return;}
if(args.Contains("--gpu-auto-live")){await GpuTests.Live();return;}
if(args.Contains("--nvidia-live")){await NvidiaTests.Live();return;}
if(args.Contains("--power-query-compare")){await PowerQueryComparison.Run(args);return;}
if(args.Contains("--ecoqos-live")){await EcoQoSTests.Live(args);return;}
if(args.Contains("--hardware-idle"))
{
    await using var sensors=new WindowsSensors(_=>ValueTask.CompletedTask,true);
    sensors.EnableTaskbarPower(args.Contains("--taskbar-power"));sensors.Start();await Task.Delay(30000);
    if(args.Contains("--clear-components")){sensors.EnableTaskbarPower(false);await Task.Delay(5000);}
    using var process=System.Diagnostics.Process.GetCurrentProcess();var before=process.TotalProcessorTime.TotalSeconds;
    var beforeCosts=sensors.CpuMilliseconds;
    var clock=System.Diagnostics.Stopwatch.StartNew();await Task.Delay(60000);process.Refresh();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{seconds=clock.Elapsed.TotalSeconds,one_core_percent=100*(process.TotalProcessorTime.TotalSeconds-before)/clock.Elapsed.TotalSeconds,
        cpu_milliseconds=sensors.CpuMilliseconds.ToDictionary(p=>p.Key,p=>p.Value-beforeCosts.GetValueOrDefault(p.Key)),devices=sensors.DeviceDiagnostics},Protocol.Json));
    return;
}

static void Equal<T>(T expected,T actual,string scenario) {if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"{scenario}: expected {expected}, got {actual}");}
var root=Path.Combine(Path.GetTempPath(),"OWY-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    EcoQoSTests.Run();
    await GpuTests.Run();
    NvidiaTests.Run();
    DirectSensorTests.Run();
    var protocol=Protocol.ParseCodex("""
        {"version":1,"sequence":1,"sampled_at":1791504000,"total_tps":120,"average_tps":40,
         "contributing_sessions":3,"pace_300_tps":2.5,"pace_300_tokens":750,"pace_partial":false,
         "coverage":"complete","issues":[],"pending_count":0,"sessions":[],"receipt_changes":[]}
        """);
    Equal(2.5d,protocol.Pace300Tps!.Value,"Python IPC five minute rate field");
    Equal(750L,protocol.Pace300Tokens,"Python IPC output count field");
    var epoch=new DateTimeOffset(2026,10,9,0,0,0,TimeSpan.Zero);
    var clock=epoch;
    using(var history=new History(root,epoch))
    {
        for(int i=0;i<900;i++)
        {
            clock=epoch.AddSeconds(i);
            var codex=new CodexReading(1,i+1,clock.ToUnixTimeSeconds(),120,40,3,1,300,false,"complete",[],0,[],[new(clock.ToUnixTimeSeconds(),i==601?300:0,true,false)]);
            history.Add(new(clock,codex,HardwareReading.Empty,[]));
        }
        Equal(0L,history.BytesWritten,"No per-second journal writes");
        var snapshot=history.View(600,clock,120);
        Equal(120d,snapshot.Bins[^1].TotalTps!.Value,"Live total aggregation");
        Equal(40d,snapshot.Bins[^1].AverageTps!.Value,"Average session aggregation");
        history.Add(new(epoch.AddSeconds(900),CodexReading.Unavailable("test gap"),HardwareReading.Empty,[]));
        Equal(true,history.BytesWritten>0,"15 minute flush writes completed minutes");
        Equal(1,history.FlushCount,"Single flush cycle");
        foreach(int range in History.Ranges)Equal(120,history.View(range,clock).Bins.Length,$"Range {range}");
        var correction=new CodexReading(1,901,epoch.AddSeconds(901).ToUnixTimeSeconds(),0,null,0,0,0,false,"complete",[],0,[],[new(epoch.AddSeconds(601).ToUnixTimeSeconds(),100,true,false)]);
        history.Add(new(epoch.AddSeconds(901),correction,HardwareReading.Empty,[]));
        history.Flush(epoch.AddSeconds(902),true);
        Equal(100L,history.View(3600,epoch.AddSeconds(902),60).OutputTokens,"Authority corrections update original minute");
    }
    string file=Directory.GetFiles(root,"*.jsonl").Single();
    File.AppendAllText(file,"{\"version\":1,\"minute\":",Encoding.UTF8);
    using(var restored=new History(root,epoch.AddMinutes(16)))
    {
        Equal(100L,restored.View(3600,epoch.AddMinutes(16),60).OutputTokens,"Restart restores latest record revision");
        Equal("Recovered an interrupted journal record",restored.Issue,"Incomplete final record recovery");
        Equal(0L,restored.BytesWritten,"Startup has no exports");
    }
    Equal("— W",Display.Power(HardwareReading.Empty),"Missing power remains explicit");
    var componentReading=HardwareReading.Empty with{ComponentWatts=28,ComponentPowerBasis="CPU+GPU"};
    Equal("CPU+GPU 28 W",Display.Power(componentReading),"Taskbar component subtotal has an explicit label");
    Equal<double?>(null,componentReading.SystemWatts,"Component subtotal never becomes whole-system power");
    Equal("25 W",Display.Power(componentReading with{SystemWatts=25,PowerBasis="Battery discharge"}),"Whole-system battery reading takes precedence");
    Equal(25d,WindowsSensors.DischargeWatts(false,false,true,-25000)!.Value,"Battery discharge in signed milliwatts");
    Equal<double?>(null,WindowsSensors.DischargeWatts(true,false,true,-25000),"AC excludes battery discharge substitute");
    Equal<double?>(null,WindowsSensors.DischargeWatts(false,true,false,25000),"Charging rate never becomes system power");
    Equal<double?>(null,WindowsSensors.DischargeWatts(false,false,true,-1),"Unknown battery rate");
    using(var line=new StreamReader(new MemoryStream("123456\n"u8.ToArray())))
    {
        bool rejected=false;try{await new BoundedLineReader(line,4).Read(CancellationToken.None);}catch(InvalidDataException){rejected=true;}
        Equal(true,rejected,"IPC input is bounded");
    }
    using(var line=new StreamReader(new MemoryStream("first\nsecond\n"u8.ToArray())))
    {
        var frames=new BoundedLineReader(line);
        Equal("first",await frames.Read(CancellationToken.None),"First buffered frame");
        Equal("second",await frames.Read(CancellationToken.None),"Remaining buffered frame is retained");
    }
    using(var output=new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out))
    using(var input=new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In,output.ClientSafePipeHandle))
    using(var reader=new StreamReader(input,leaveOpen:true))
    {
        string expected=new('x',1023);await output.WriteAsync(Encoding.UTF8.GetBytes(expected+"\n"));
        using var deadline=new CancellationTokenSource(1000);
        Equal(expected,await new BoundedLineReader(reader,2048).Read(deadline.Token),"A full pipe block is delivered without waiting for another frame");
    }
    var shortRoot=Path.Combine(root,"restart-minute");
    using(var h=new History(shortRoot,epoch.AddSeconds(100)))
    {
        var c=new CodexReading(1,1,epoch.AddSeconds(100).ToUnixTimeSeconds(),20,10,2,0,0,false,"complete",[],0,[],[new(epoch.AddSeconds(100).ToUnixTimeSeconds(),100,true,false)]);
        h.Add(new(epoch.AddSeconds(100),c,HardwareReading.Empty,[]));h.Flush(epoch.AddSeconds(101),true);
    }
    using(var h=new History(shortRoot,epoch.AddSeconds(110)))
    {
        var c=new CodexReading(1,1,epoch.AddSeconds(110).ToUnixTimeSeconds(),0,null,0,0,0,false,"complete",[],0,[],[]);
        h.Add(new(epoch.AddSeconds(110),c,HardwareReading.Empty,[]));
        Equal(100L,h.View(600,epoch.AddSeconds(111),120).OutputTokens,"Restart preserves earlier output within the same minute");
    }
    using(var h=new History(Path.Combine(root,"receipt-rates"),epoch))
    {
        var c=new CodexReading(1,1,epoch.AddSeconds(2).ToUnixTimeSeconds(),15,5,3,7496/300d,7496,true,"partial",[],1,[],
            [new(epoch.ToUnixTimeSeconds(),0,true,true),new(epoch.AddSeconds(1).ToUnixTimeSeconds(),7496,true,true)]);
        h.Add(new(epoch.AddSeconds(2),c,HardwareReading.Empty,[]));
        var view=h.View(600,epoch.AddSeconds(3),600);
        Equal(7496L,view.OutputTokens,"Reported output is counted even with partial generation timing");
        Equal(7496d,view.Bins.Single(x=>x.OutputTokens==7496).ReceivedTps!.Value,"A one second bar represents received output");
        Equal(0d,view.Bins.Single(x=>x.Start==epoch.ToUnixTimeSeconds()).ReceivedTps!.Value,"Observed waiting is a receipt zero");
        Equal<double?>(null,view.Bins[^1].ReceivedTps,"Generation samples never invent missing receipt observations");
        Equal(15d,view.Bins[^1].TotalTps!.Value,"Generation history retains its separate basis");
        foreach(int range in History.Ranges)
        {
            var aggregated=h.View(range,epoch.AddSeconds(60),120);
            Equal(7496L,aggregated.OutputTokens,$"Receipt tokens are preserved in range {range}");
            double integrated=aggregated.Bins.Sum(x=>(x.ReceivedTps??0)*(x.End-x.Start));
            Equal(true,Math.Abs(integrated-7496)<1e-6,$"Receipt rates integrate to the same output in range {range}");
        }
        var correction=c with{Sequence=2,SampledAt=epoch.AddSeconds(4).ToUnixTimeSeconds(),ReceiptChanges=[new(epoch.AddSeconds(1).ToUnixTimeSeconds(),0,true,true)]};
        h.Add(new(epoch.AddSeconds(4),correction,HardwareReading.Empty,[]));
        Equal(0L,h.View(600,epoch.AddSeconds(5),600).OutputTokens,"Receipt authority correction updates both chart units");
        Equal(0L,h.BytesWritten,"Receipt chart queries do not add SSD writes");
    }
    HardwareReading? sensorSample=null;
    await using(var sensors=new WindowsSensors(value=>{sensorSample=value;return ValueTask.CompletedTask;}))
    {sensors.Start();await Task.Delay(3500);}
    Equal(true,sensorSample?.MemoryTotalBytes>0,"Windows memory API returns local RAM");
    Equal(true,sensorSample?.CpuPercent is >=0 and <=100,"Windows CPU delta is valid");
    if(sensorSample?.OnBattery==false)Equal<double?>(null,sensorSample.SystemWatts,"AC component readings remain separate from whole-system watts");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{hardware_sample=sensorSample},Protocol.Json));
    Console.WriteLine("OWY history regression checks passed");
}
finally {Directory.Delete(root,true);}
