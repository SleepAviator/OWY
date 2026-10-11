using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using OWY.Core;
using OWY.Hardware;
using OWY.Taskbar;

namespace OWY.App;

public sealed class PopupWindow : Window
{
    readonly MonitorEngine engine;readonly string data;readonly Func<Task> quit;readonly Action<bool> configurePower,configureDetails,configureAuxiliary;
    readonly StackPanel root=new(){Spacing=14,Padding=new(20)};
    readonly TextBlock total=new(){FontSize=27},average=new(){FontSize=21},pace=new(){FontSize=21},coverage=new(){FontSize=12,Opacity=.75,TextWrapping=TextWrapping.Wrap};
    readonly TextBlock historyTotal=new(){FontSize=16},issue=new(){FontSize=12,TextWrapping=TextWrapping.Wrap};
    readonly TextBlock sessions=new(){FontSize=12,TextWrapping=TextWrapping.Wrap};
    readonly TextBlock placement=new(){FontSize=12,TextWrapping=TextWrapping.Wrap};
    readonly BarChart speedChart=new(false),tokensChart=new(true);
    readonly ComboBox range=new(){HorizontalAlignment=HorizontalAlignment.Right,MinWidth=130};
    readonly Dictionary<string,TextBlock> hardwareLabels=[];
    readonly StackPanel detailRows=new(){Spacing=6};
    readonly Expander sensorDetails;
    Settings settings;TaskbarHost? strip;
    bool visible,closing,queryPending;long nextChart;int chartRange;
    string? actionIssue;
    readonly nint hwnd;
    readonly bool isPreview;
    public bool IsVisible=>visible;
    public object Diagnostics=>new{visible,range_seconds=settings.RangeSeconds,total_text=total.Text,average_text=average.Text,pace_text=pace.Text,power_text=hardwareLabels["Power"].Text,
        gpu_text=hardwareLabels["GPU"].Text,theme=root.ActualTheme.ToString(),speed_chart_updates=speedChart.UpdateCount,token_chart_updates=tokensChart.UpdateCount,hover_index=speedChart.HoverIndex,
        sensor_details_expanded=sensorDetails.IsExpanded,sensor_row_count=sensorDetails.IsExpanded?detailRows.Children.OfType<TextBlock>().Count(t=>t.Visibility==Visibility.Visible):0};
    static readonly string[] rangeNames=["10 Minutes","1 Hour","3 Hours","6 Hours","12 Hours","1 Day","3 Days","7 Days","14 Days","28 Days"];
    public PopupWindow(MonitorEngine engine,Settings settings,string data,Func<Task> quit,Action<bool> configurePower,Action<bool> configureDetails,Action<bool> configureAuxiliary,bool isPreview=false)
    {
        this.engine=engine;this.settings=settings;this.data=data;this.quit=quit;this.configurePower=configurePower;this.configureDetails=configureDetails;this.configureAuxiliary=configureAuxiliary;this.isPreview=isPreview;Title="OWY · Owl’s Watching You";
        if(isPreview){root.RequestedTheme=ElementTheme.Light;root.Background=new SolidColorBrush(Windows.UI.Color.FromArgb(255,249,249,249));}
        var scroll=new ScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        scroll.Background=(Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];Content=scroll;
        var header=new Grid();header.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text="OWY",FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        var close=new Button{Content="✕",MinWidth=32,Padding=new(7)};
        close.Click+=async(_,_)=>{if(isPreview)await quit();else Hide();};Grid.SetColumn(close,1);header.Children.Add(close);root.Children.Add(header);
        if(isPreview)root.Children.Add(new TextBlock{Text="Preview · fictional readings",FontSize=12,Opacity=.7});
        root.Children.Add(new TextBlock{Text="Codex",FontSize=17,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        var live=new Grid{ColumnSpacing=16};for(int i=0;i<3;i++)live.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        AddMetric(live,"Generation total",total,0);AddMetric(live,"Avg / session",average,1);AddMetric(live,"5 min received",pace,2);root.Children.Add(live);root.Children.Add(coverage);
        var historyHead=new Grid();historyHead.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});historyHead.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        historyHead.Children.Add(new TextBlock{Text="Received output · t/s",VerticalAlignment=VerticalAlignment.Center,FontSize=13});
        foreach(var name in rangeNames)range.Items.Add(name);range.SelectedIndex=Array.IndexOf(History.Ranges,settings.RangeSeconds);
        range.SelectionChanged+=(_,_)=>
        {
            if(range.SelectedIndex<0 || range.SelectedIndex>=History.Ranges.Length || History.Ranges[range.SelectedIndex]==this.settings.RangeSeconds)return;
            this.settings=this.settings with{RangeSeconds=History.Ranges[range.SelectedIndex]};Save();nextChart=0;_ = RefreshCharts();
        };
        Grid.SetColumn(range,1);historyHead.Children.Add(range);root.Children.Add(historyHead);root.Children.Add(speedChart);
        root.Children.Add(historyTotal);root.Children.Add(tokensChart);
        var sessionGroup=new Expander{Header="Sessions",Content=sessions,HorizontalAlignment=HorizontalAlignment.Stretch};root.Children.Add(sessionGroup);
        var hardware=new Grid{ColumnSpacing=20,RowSpacing=8};for(int i=0;i<2;i++)hardware.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        string[] labels=["CPU","Memory","GPU","Network","Storage","Battery","Power"];
        for(int i=0;i<labels.Length;i++)
        {
            if(i%2==0)hardware.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var text=new TextBlock{Text=labels[i]+"  —",FontSize=13,TextWrapping=TextWrapping.Wrap};hardwareLabels[labels[i]]=text;
            Grid.SetColumn(text,i%2);Grid.SetRow(text,i/2);hardware.Children.Add(text);
        }
        root.Children.Add(new TextBlock{Text="Hardware",FontSize=17,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});root.Children.Add(hardware);
        sensorDetails=new(){Header="Sensor details",Content=detailRows,HorizontalAlignment=HorizontalAlignment.Stretch};
        sensorDetails.RegisterPropertyChangedCallback(Expander.IsExpandedProperty,(_,_)=>configureAuxiliary(visible && sensorDetails.IsExpanded));
        root.Children.Add(sensorDetails);
        var settingsPanel=SettingsPanel();if(isPreview && settingsPanel is Control previewSettings)previewSettings.IsEnabled=false;root.Children.Add(settingsPanel);
        root.Children.Add(new TextBlock{Text="Generation uses 5s estimates. Received output counts usage reports; 5 min avg = output over 300s, including idle time.",FontSize=11,Opacity=.6,TextWrapping=TextWrapping.Wrap});
        root.Children.Add(issue);
        var stop=new Button{Content="Stop and quit",HorizontalAlignment=HorizontalAlignment.Right};stop.Click+=async(_,_)=>await quit();root.Children.Add(stop);
        hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"assets","OWY.ico"));
        long extended=Native.GetWindowLongPtr(hwnd,-20).ToInt64();Native.SetWindowLongPtr(hwnd,-20,(nint)((extended|0x80)&~0x40000));
        AppWindow.IsShownInSwitchers=false;
        var presenter=OverlappedPresenter.Create();presenter.IsResizable=false;presenter.IsMaximizable=false;presenter.IsMinimizable=false;presenter.IsAlwaysOnTop=true;presenter.SetBorderAndTitleBar(true,false);AppWindow.SetPresenter(presenter);
        AppWindow.Resize(new(520,800));AppWindow.Closing+=async(_,e)=>{if(!closing){e.Cancel=true;if(isPreview)await quit();else Hide();}};
        Activated+=(_,e)=>{if(!isPreview && e.WindowActivationState==WindowActivationState.Deactivated && visible)Hide();};
        var ui=new Windows.UI.ViewManagement.UISettings();if(ui.AdvancedEffectsEnabled)SystemBackdrop=new MicaBackdrop();
        // XAML resources and app theme adapt independently of taskbar SystemUsesLightTheme.
        root.ActualThemeChanged+=(_,_)=>Refresh();
    }
    static void AddMetric(Grid grid,string label,TextBlock value,int column)
    {
        var stack=new StackPanel{Spacing=3};stack.Children.Add(new TextBlock{Text=label,FontSize=12,Opacity=.7});stack.Children.Add(value);Grid.SetColumn(stack,column);grid.Children.Add(stack);
    }
    UIElement SettingsPanel()
    {
        var panel=new StackPanel{Spacing=9};panel.Children.Add(placement);
        CheckBox Toggle(string text,bool initial,Action<bool> apply)
        {
            var control=new CheckBox{Content=text,IsChecked=initial};control.Click+=(_,_)=>{apply(control.IsChecked==true);Save();};panel.Children.Add(control);return control;
        }
        Toggle("Show taskbar readings",settings.ShowTaskbar,v=>{settings=settings with{ShowTaskbar=v};ConfigureStrip();});
        Toggle("Show network",settings.ShowNetwork,v=>{settings=settings with{ShowNetwork=v};ConfigureStrip();});
        Toggle("Show watts",settings.ShowWatts,v=>{settings=settings with{ShowWatts=v};ConfigureStrip();});
        Toggle("Launch at sign-in",settings.LaunchAtSignIn,v=>{settings=settings with{LaunchAtSignIn=v};settings.ApplySignIn(Environment.ProcessPath!);});
        panel.Children.Add(new TextBlock{Text="Sensors use Windows device queries and installed GPU drivers. Extra sensors are read only while Sensor details is expanded. Inactive NVIDIA is not queried. APU includes CPU + iGPU; power is a component subtotal.",FontSize=11,Opacity=.7,TextWrapping=TextWrapping.Wrap});
        var config=new Button{Content="Enable Codex side-chat telemetry"};config.Click+=async(_,_)=>
        {
            var process=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"python","python.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            process.ArgumentList.Add("-B");process.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"worker","configure_telemetry.py"));
            using var p=Process.Start(process)!;string output=await p.StandardOutput.ReadToEndAsync();string error=await p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();
            ShowIssue(p.ExitCode==0?"Telemetry configured. Restart Codex yourself when your current work is saved.":"Telemetry configuration failed: "+error[..Math.Min(error.Length,240)]);
        };panel.Children.Add(config);
        panel.Children.Add(new TextBlock{Text="Restart Codex yourself after enabling telemetry. OWY does not close your chats.",FontSize=11,Opacity=.7,TextWrapping=TextWrapping.Wrap});
        return new Expander{Header="Settings",Content=panel,HorizontalAlignment=HorizontalAlignment.Stretch};
    }
    void ConfigureStrip(){strip?.Configure(settings.ShowTaskbar,settings.ShowNetwork,settings.ShowWatts);configurePower(settings.ShowTaskbar && settings.ShowWatts);}
    void Save(){try{settings.Save(Path.Combine(data,"settings.json"));}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){ShowIssue("Settings save failed: "+ex.Message);}}
    public void SetStrip(TaskbarHost host){strip=host;host.PlacementChanged+=r=>placement.Text=r.Diagnostic;placement.Text=host.Report.Diagnostic;}
    public void Toggle(){if(visible)Hide();else Show();}
    public void Show()
    {
        visible=true;configureDetails(true);configureAuxiliary(sensorDetails.IsExpanded);var display=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest);var work=display.WorkArea;
        int scale=(int)Math.Max(96,Native.GetDpiForWindow(hwnd));int width=520*scale/96,height=Math.Min(800*scale/96,work.Height-16);
        AppWindow.MoveAndResize(new(work.X+work.Width-width-8,work.Y+work.Height-height-8,width,height));
        AppWindow.Show();Activate();Refresh();nextChart=0;_ = RefreshCharts();
    }
    void Hide(){visible=false;configureAuxiliary(false);configureDetails(false);AppWindow.Hide();}
    public void ShowIssue(string text){actionIssue=text;issue.Text=text;}
    public void CloseForQuit(){closing=true;Close();}
    public void Refresh()
    {
        if(!visible)return;
        var value=engine.Latest;var c=value.Codex;var h=value.Hardware;
        bool fresh=isPreview || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0-c.SampledAt<3;
        total.Text=Display.Rate(fresh?c.TotalTps:null);average.Text=Display.Rate(fresh?c.AverageTps:null);pace.Text=Display.Rate(fresh?c.Pace300Tps:null);
        coverage.Text=$"{c.ContributingSessions} contributing · "+(fresh?c.Coverage:"stale")+(c.PendingCount>0?$" · {c.PendingCount} pending":"")+(h.BatterySaver?" · Battery Saver":"");
        sessions.Text=c.Sessions.Length==0?"No contributing sessions":string.Join("\n",c.Sessions.Take(12).Select(s=>$"{Display.Rate(s.Tps)}  {s.Provider} · {s.Model}"))+(c.Sessions.Length>12?$"\n+{c.Sessions.Length-12} sessions":"");
        hardwareLabels["CPU"].Text="CPU  "+Percent(h.CpuPercent);hardwareLabels["Memory"].Text="Memory  "+Percent(h.MemoryPercent);
        hardwareLabels["GPU"].Text=h.ActiveGpuKind=="unavailable"?"GPU  Unavailable":
            (h.ActiveGpuKind=="integrated"?"iGPU  ":"dGPU  ")+h.ActiveGpu+" · "+Display.Watts(h.ActiveGpuWatts);
        hardwareLabels["Network"].Text=$"↑ {Display.Bytes(h.UploadBytesPerSecond)}  ↓ {Display.Bytes(h.DownloadBytesPerSecond)}";
        hardwareLabels["Storage"].Text="Storage  "+Display.Bytes(h.DiskBytesPerSecond);hardwareLabels["Battery"].Text="Battery  "+Percent(h.BatteryPercent)+(h.OnBattery?"":" · AC");
        hardwareLabels["Power"].Text=Display.Power(h)+(h.SystemWatts.HasValue?" · battery":h.ComponentWatts.HasValue?" · subtotal":" · unavailable");
        if(visible)
        {
            if(sensorDetails.IsExpanded)
            {
                var details=h.Details.Take(100).ToArray();
                while(detailRows.Children.Count<details.Length)detailRows.Children.Add(new TextBlock{FontSize=12,TextWrapping=TextWrapping.Wrap});
                for(int i=0;i<detailRows.Children.Count;i++)
                {
                    var text=(TextBlock)detailRows.Children[i];text.Visibility=i<details.Length?Visibility.Visible:Visibility.Collapsed;
                    if(i<details.Length){var sensor=details[i];text.Text=sensor.Name+"  "+(sensor.Value is {} n?sensor.Unit=="bytes"?Display.Size(n):n.ToString("0.#")+" "+sensor.Unit:sensor.Status=="stale"?"Stale":"Unavailable");}
                }
            }
            var gaps=h.Issues.Concat(c.Issues).Concat(value.Issues).Take(4).ToArray();
            issue.Text=gaps.Length>0?string.Join(" · ",gaps):actionIssue??"";
            if(Environment.TickCount64>=nextChart){nextChart=Environment.TickCount64+5000;_ = RefreshCharts();}
        }
    }
    static string Percent(double? value)=>value is {} n?n.ToString("0.#")+"%":"Unavailable";
    public async Task ExportPreviewAsync(string path,bool expanded)
    {
        if(!isPreview)throw new InvalidOperationException("Screenshot export is only available for fictional previews");
        sensorDetails.IsExpanded=expanded;Refresh();
        while(queryPending)await Task.Delay(10);
        await RefreshCharts();root.UpdateLayout();
        await Task.Delay(150); // Let native chart and Expander layout complete.
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(root);
        if(bitmap.PixelWidth==0 || bitmap.PixelHeight==0)throw new InvalidOperationException("Native preview did not render");
        var pixels=await bitmap.GetPixelsAsync();
        var folder=await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var file=await folder.CreateFileAsync(Path.GetFileName(path),CreationCollisionOption.ReplaceExisting);
        using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());
        await encoder.FlushAsync();
    }
    async Task RefreshCharts()
    {
        if(!visible || queryPending)return;queryPending=true;chartRange=settings.RangeSeconds;
        try
        {
            var history=await engine.View(chartRange,speedChart.DesiredCount);
            if(chartRange!=settings.RangeSeconds){nextChart=0;return;}
            speedChart.Update(history.Bins);tokensChart.Update(history.Bins);historyTotal.Text=$"Received output  {Display.Tokens(history.OutputTokens)} tokens"+(history.Coverage<1?$" · {history.Coverage:P0} covered":"");
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException){ShowIssue("History unavailable: "+ex.Message);}
        finally{queryPending=false;}
    }
    static class Native
    {
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]internal static extern nint GetWindowLongPtr(nint hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]internal static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
        [DllImport("user32.dll")]internal static extern uint GetDpiForWindow(nint hwnd);
    }
}
