using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using OWY.Core;
using OWY.Hardware;
using OWY.Taskbar;

namespace OWY.App;

public partial class App : Application
{
    MonitorEngine? engine;CodexClient? codex;WindowsSensors? hardware;TaskbarHost? strip;PopupWindow? window;
    static readonly bool preview=Environment.GetCommandLineArgs().Contains("--demo");
    readonly string data=preview?Path.Combine(Path.GetTempPath(),"OWY-preview-"+Guid.NewGuid().ToString("N")):
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OWY");
    Mutex? mutex;bool shuttingDown;int pending;
    volatile bool suspended;long updates;
    Settings? currentSettings;string? settingsIssue;
    public App()
    {
        Directory.CreateDirectory(data);
        UnhandledException+=(_,e)=>File.WriteAllText(Path.Combine(data,"unhandled-error.txt"),e.Exception.ToString());
        try{InitializeComponent();}
        catch(Exception ex){File.WriteAllText(Path.Combine(data,"startup-error.txt"),ex.ToString());throw;}
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if(preview){_ = OpenPreview();return;}
        mutex=new Mutex(true,"Local\\OWY."+Environment.UserName,out bool first);
        if(!first){Exit();return;}
        try
        {
            Directory.CreateDirectory(data);
            var settings=Settings.Load(Path.Combine(data,"settings.json"),out settingsIssue);currentSettings=settings;
            engine=new(Path.Combine(data,"history"));
            strip=new(Path.Combine(AppContext.BaseDirectory,"assets","OWY.ico"));
            strip.Configure(settings.ShowTaskbar,settings.ShowNetwork,settings.ShowWatts);
            strip.Clicked+=()=>OpenPopup();strip.QuitRequested+=()=>_ = Quit();
            hardware=new(engine.Publish);
            hardware.EnableTaskbarPower(settings.ShowTaskbar && settings.ShowWatts);
            strip.SamplingSuspended+=v=>{suspended=v;hardware.Suspend(v);};
            suspended=strip.IsSamplingSuspended;hardware.Suspend(suspended);
            codex=new(AppContext.BaseDirectory,engine.Publish);
            var dispatcher=DispatcherQueue.GetForCurrentThread();
            engine.Failed+=ex=>dispatcher.TryEnqueue(()=>
            {
                File.WriteAllText(Path.Combine(data,"accounting-error.txt"),ex.ToString());
                _ = Quit();
            });
            engine.Changed+=()=>
            {
                // Accounting continues off-thread while the desktop is hidden.
                // WTS/power notifications resume the UI on the next live frame.
                if(suspended)return;
                if(Interlocked.Exchange(ref pending,1)!=0)return;
                dispatcher.TryEnqueue(()=>
                {
                    Interlocked.Exchange(ref pending,0);
                    if(shuttingDown || suspended)return;
                    updates++;
                    var value=engine.Latest;var c=value.Codex;var h=value.Hardware;
                    bool fresh=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0-c.SampledAt<3;
                    strip.Update("↑ "+Display.Bytes(h.UploadBytesPerSecond),"↓ "+Display.Bytes(h.DownloadBytesPerSecond),Display.Rate(fresh?c.TotalTps:null),Display.Power(h));
                    window?.Refresh();
                });
            };
            hardware.Start();codex.Start();
            if(Environment.GetCommandLineArgs().Contains("--show"))OpenPopup();
            int at=Array.IndexOf(Environment.GetCommandLineArgs(),"--quit-after");
            if(at>=0 && int.TryParse(Environment.GetCommandLineArgs().ElementAtOrDefault(at+1),out int seconds) && seconds>0)
                _ = StopAfter(seconds);
        }
        catch(Exception ex)
        {
            // Startup failures remain inspectable and are never silently swallowed.
            File.WriteAllText(Path.Combine(data,"startup-error.txt"),ex.ToString());
            _ = Quit();
        }
    }
    async Task OpenPreview()
    {
        string? output=null;
        try
        {
            var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"--screenshots");
            if(at>=0)
            {
                if(at+1>=args.Length || args[at+1].StartsWith("--"))throw new ArgumentException("--screenshots requires an output directory");
                output=Path.GetFullPath(args[at+1]);Directory.CreateDirectory(output);
            }
            var now=DateTimeOffset.UtcNow;
            PreviewData.SeedHistory(Path.Combine(data,"history"),now);
            engine=new(Path.Combine(data,"history"));
            await engine.Publish(PreviewData.Codex(now));await engine.Publish(PreviewData.Hardware(now));
            await engine.View(600,120); // Wait for accounting before opening the native controls.
            window=new(engine,new Settings(ShowTaskbar:false,RangeSeconds:600),data,QuitPreview,_=>{},_=>{},_=>{},isPreview:true);
            window.Show();
            if(output is not null)
            {
                await Task.Delay(300);
                await window.ExportPreviewAsync(Path.Combine(output,"preview-collapsed.png"),expanded:false);
                await window.ExportPreviewAsync(Path.Combine(output,"preview-expanded.png"),expanded:true);
                File.WriteAllText(Path.Combine(output,"preview-status.json"),System.Text.Json.JsonSerializer.Serialize(new{version=1,fictional=true,sensors_started=false,codex_started=false,window=window.Diagnostics},Protocol.Json));
                await QuitPreview();
            }
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(output??Path.GetTempPath(),"OWY-preview-error.txt"),ex.ToString());
            Environment.ExitCode=1;await QuitPreview();
        }
    }
    async Task QuitPreview()
    {
        if(shuttingDown)return;shuttingDown=true;
        try{if(engine is not null)await engine.DisposeAsync();}
        finally{window?.CloseForQuit();Directory.Delete(data,true);Exit();}
    }
    void OpenPopup()
    {
        if(window is null){window=new(engine!,currentSettings!,data,Quit,v=>hardware!.EnableTaskbarPower(v),v=>hardware!.EnableDetails(v),v=>hardware!.EnableAuxiliaryDetails(v));window.SetStrip(strip!);if(settingsIssue is not null)window.ShowIssue(settingsIssue);}
        window.Toggle();
    }
    async Task StopAfter(int seconds){await Task.Delay(TimeSpan.FromSeconds(seconds));await Quit();}
    async Task Quit()
    {
        if(shuttingDown)return;shuttingDown=true;
        try
        {
            var cleanup=new List<Task>();
            if(codex is not null)cleanup.Add(codex.DisposeAsync().AsTask());
            if(hardware is not null)cleanup.Add(hardware.DisposeAsync().AsTask());
            var workers=Task.WhenAll(cleanup);Exception? failure=null;
            try{await workers;}catch{failure=workers.Exception;}
            try{if(engine is not null)await engine.DisposeAsync();}
            catch(Exception ex){failure=failure is null?ex:new AggregateException(failure,ex);}
            if(failure is not null)File.WriteAllText(Path.Combine(data,"shutdown-error.txt"),failure.ToString());
            var c=engine?.Latest.Codex;var h=engine?.Latest.Hardware;
            var status=new{version=1,closed_at=DateTimeOffset.UtcNow,worker_stopped=codex?.ProcessId==0,journal_bytes=engine?.JournalBytes,
                journal_flushes=engine?.JournalFlushes,placement=strip?.Report,paints=strip?.PaintCount,layouts=strip?.LayoutCount,geometry_scans=strip?.GeometryScans,geometry_milliseconds=strip?.GeometryMilliseconds,ui_updates=updates,
                worker_sequence=c?.Sequence,worker_ecoqos_applied=codex?.EcoQoSApplied,hardware_ecoqos_cycles=hardware?.EcoQoSCycles,
                total_tps=c?.TotalTps,average_tps=c?.AverageTps,pace_300_tps=c?.Pace300Tps,
                telemetry_received_at=c?.TelemetryReceivedAt,telemetry_batches=c?.TelemetryBatches,
                cleanup_succeeded=failure is null,contributing_sessions=c?.ContributingSessions,coverage=c?.Coverage,issues=c?.Issues,cpu_percent=h?.CpuPercent,memory_percent=h?.MemoryPercent,
                upload_bytes_per_second=h?.UploadBytesPerSecond,download_bytes_per_second=h?.DownloadBytesPerSecond,system_watts=h?.SystemWatts,
                component_watts=h?.ComponentWatts,component_power_basis=h?.ComponentPowerBasis,power_text=h is null?null:Display.Power(h),taskbar_rows=strip?.RenderedRows,
                active_gpu=h?.ActiveGpu,active_gpu_kind=h?.ActiveGpuKind,active_gpu_watts=h?.ActiveGpuWatts,gpu_selection=hardware?.GpuSelection,
                amd=hardware?.AmdReading,nvidia=hardware?.NvidiaReading,nvidia_diagnostics=hardware?.NvidiaDiagnostics,direct_devices=hardware?.DirectReading,device_diagnostics=hardware?.DeviceDiagnostics,hardware_issues=h?.Issues,session_state_issue=strip?.SessionStateIssue,window=window?.Diagnostics};
            File.WriteAllText(Path.Combine(data,"last-run.json"),System.Text.Json.JsonSerializer.Serialize(status,Protocol.Json));
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(data,"shutdown-error.txt"),ex.ToString());}
        finally{strip?.Dispose();window?.CloseForQuit();mutex?.Dispose();Exit();}
    }
}
