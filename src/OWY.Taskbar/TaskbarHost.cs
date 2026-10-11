using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32;

namespace OWY.Taskbar;

public sealed record PlacementReport(bool Attached, string Diagnostic, string Taskbar, string Tray, string Strip, uint Dpi, int ItemCount, string[] Buttons);

// Owns only OWY windows. Explorer is observed, never resized, patched or injected.
public sealed class TaskbarHost : IDisposable
{
    readonly Native.WindowProc proc;
    readonly Native.WinEventProc events;
    readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    nint owner, strip, parent, font, hook, ownIcon;
    Native.NotifyIcon icon;
    Native.Rect stripRect;
    string[] rows = ["↑ —  ·  — t/s", "↓ —  ·  — W"];
    string up="↑ —",down="↓ —",codex="— t/s",watts="— W";
    bool showNetwork=true,showWatts=true,geometryDirty=true;
    Native.Rect[] occupied=[];
    string geometryKey="";
    uint fontDpi;
    long nextGeometry;
    bool disposed, enabled = true,locked,sleeping;
    int columns = 2;
    long nextLayout;
    PlacementReport report = new(false,"Starting", "", "", "",96,0,[]);
    public PlacementReport Report => report;
    public string[] RenderedRows=>report.Attached?rows.ToArray():[];
    public event Action<PlacementReport>? PlacementChanged;
    public event Action? Clicked;
    public event Action? QuitRequested;
    public event Action<bool>? SamplingSuspended;
    public bool IsSamplingSuspended=>locked||sleeping;
    public string? SessionStateIssue {get;private set;}
    public long PaintCount {get;private set;}
    public long LayoutCount {get;private set;}
    public long GeometryScans {get;private set;}
    public double GeometryMilliseconds {get;private set;}
    public TaskbarHost(string? iconPath=null)
    {
        proc = Handle;
        events = (_, _, _, _, _, _, _) => {geometryDirty=true;Native.PostMessage(owner,0x8002,0,0);};
        var cls=new Native.WindowClass { Size=(uint)Marshal.SizeOf<Native.WindowClass>(),Proc=proc,Instance=Native.GetModuleHandle(null),Name="OWY.OwnedStrip",Cursor=Native.LoadCursor(0,32512) };
        if(Native.RegisterClassEx(ref cls)==0) throw new Win32Exception(Marshal.GetLastWin32Error(),"Register OWY window class failed");
        owner=Native.CreateWindowEx(0x80,cls.Name,"OWY",0x80000000,0,0,0,0,0,0,cls.Instance,0);
        if(owner==0) throw new Win32Exception(Marshal.GetLastWin32Error(),"Create OWY owner window failed");
        if(iconPath is not null){ownIcon=Native.LoadImage(0,iconPath,1,32,32,0x10);if(ownIcon==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"Load OWY owl icon failed");}
        icon=new Native.NotifyIcon { Size=(uint)Marshal.SizeOf<Native.NotifyIcon>(),Hwnd=owner,Id=1,Flags=7,Callback=0x8001,Icon=ownIcon!=0?ownIcon:Native.LoadIcon(0,32516),Tip="OWY · Owl’s Watching You",Info="",Title="" };
        AddTray();
        if(Native.WTSRegisterSessionNotification(owner,0))ReadSessionState();
        else {locked=true;SessionStateIssue="Windows session notifications unavailable (error "+Marshal.GetLastWin32Error()+")";}
        Native.SetTimer(owner,1,IsSamplingSuspended?30000u:2000u,0);
        Place();
    }
    void ReadSessionState()
    {
        // Registration does not replay a lock that happened before startup.
        // WTSINFOEX on Windows 11 has Level at 0 and its aligned Level1
        // prefix at 8: SessionId, ConnectState, SessionFlags.
        if(Native.WTSQuerySessionInformation(0,uint.MaxValue,25,out var state,out uint bytes))
        {
            try
            {
                if(bytes>=20 && Marshal.ReadInt32(state)==1)
                {locked=Marshal.ReadInt32(state,12)!=0 || Marshal.ReadInt32(state,16)!=1;SessionStateIssue=null;}
                else {locked=true;SessionStateIssue="Windows session state is unavailable";}
            }
            finally {Native.WTSFreeMemory(state);}
        }
        else {locked=true;SessionStateIssue="Windows session state unavailable (error "+Marshal.GetLastWin32Error()+")";}
    }
    public void SetVisible(bool value) { enabled=value; Place(); }
    public void Update(string networkUp,string networkDown,string codex,string watts)
    {
        if(up==networkUp && down==networkDown && this.codex==codex && this.watts==watts)return;
        up=networkUp;down=networkDown;this.codex=codex;this.watts=watts;
        // Values can become wider without a scale change; placement re-measures them.
        if(!IsSamplingSuspended)Native.PostMessage(owner,0x8002,0,0);
    }
    public void Configure(bool visible,bool network,bool power){enabled=visible;showNetwork=network;showWatts=power;Place();}
    string[] Rows(int count)=>count==2 && showNetwork?[$"{up}  ·  {codex}",$"{down}"+(showWatts?$"  ·  {watts}":"")]:[codex,count>=1 && showWatts?watts:""];
    void AddTray() { if(!Native.Shell_NotifyIcon(0,ref icon)) SetReport(new(false,"Notification tray registration failed", "", "", "",96,0,[])); }
    void SetReport(PlacementReport next) { if(report.Attached==next.Attached && report.Diagnostic==next.Diagnostic && report.Taskbar==next.Taskbar && report.Tray==next.Tray && report.Strip==next.Strip && report.Dpi==next.Dpi && report.ItemCount==next.ItemCount && report.Buttons.SequenceEqual(next.Buttons))return;bool diagnosticChanged=report.Diagnostic!=next.Diagnostic;report=next;if(diagnosticChanged){icon.Tip=("OWY · "+next.Diagnostic)[..Math.Min(127,("OWY · "+next.Diagnostic).Length)];Native.Shell_NotifyIcon(1,ref icon);}PlacementChanged?.Invoke(next); }
    static Native.Rect Bounds(AutomationElement el)
    {
        var r=el.Current.BoundingRectangle;
        return new Native.Rect{Left=(int)r.Left,Top=(int)r.Top,Right=(int)r.Right,Bottom=(int)r.Bottom};
    }
    public void Place()
    {
        if(disposed) return;
        if(IsSamplingSuspended){SetReport(report with{Diagnostic="Taskbar updates paused while locked or asleep"});return;}
        LayoutCount++;
        var taskbar=Native.FindWindow("Shell_TrayWnd",null);
        if(!enabled) { Native.ShowWindow(strip,0); SetReport(report with{Attached=false,Diagnostic="Taskbar readings hidden"});return; }
        if(taskbar==0 || !Native.GetWindowRect(taskbar,out var bar)) { Hide("Waiting for Explorer taskbar"); return; }
        var tray=Native.FindWindowEx(taskbar,0,"TrayNotifyWnd",null);
        if(tray==0 || !Native.GetWindowRect(tray,out var trayRect) || trayRect.Width<=0) { Hide("Unsupported taskbar: tray bounds unavailable"); return; }
        if(bar.Width<bar.Height || trayRect.Left<=bar.Left || bar.Height>160) { Hide("Only a horizontal bottom taskbar is supported");return; }
        var dpi=Native.GetDpiForWindow(taskbar);
        double scale=dpi/96.0;
        string key=bar+"/"+trayRect+"/"+taskbar;
        if(geometryDirty || key!=geometryKey || Environment.TickCount64>=nextGeometry)try
        {
            var scanning=System.Diagnostics.Stopwatch.StartNew();GeometryScans++;
            var root=AutomationElement.FromHandle(taskbar);
            var controls=root.FindAll(TreeScope.Descendants,new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.TabItem)));
            occupied=controls.Cast<AutomationElement>().Where(e=>e.Current.ProcessId!=Environment.ProcessId && !e.Current.IsOffscreen).Select(Bounds).Where(r=>r.Width>0 && r.Height>0 && r.Width<bar.Width/2 && r.Intersects(bar)).ToArray();
            GeometryMilliseconds+=scanning.Elapsed.TotalMilliseconds;
            geometryDirty=false;geometryKey=key;nextGeometry=Environment.TickCount64+30_000;
        }
        catch(Exception ex) when(ex is ElementNotAvailableException or InvalidOperationException or COMException)
        { Hide("Cannot verify free taskbar space ("+ex.GetType().Name+")");return; }
        if(occupied.Length==0){geometryDirty=true;Hide("Taskbar buttons unavailable; readings remain in the tray");return;}
        var context=Native.GetWindowDpiAwarenessContext(taskbar);
        if(parent!=taskbar || !Native.IsWindow(strip))
        {
            if(strip!=0 && Native.IsWindow(strip)) Native.DestroyWindow(strip);
            var old=Native.SetThreadDpiAwarenessContext(context);
            try
            {
                // A hidden top-level window avoids inheriting our PMv2 owner's
                // different DPI context during creation. Convert it to a child
                // before parenting, following SetParent's style requirements.
                strip=Native.CreateWindowEx(0x80000|0x80,"OWY.OwnedStrip","OWY readings",0x80000000,0,0,1,1,0,0,Native.GetModuleHandle(null),0);
                if(strip==0) throw new Win32Exception(Marshal.GetLastWin32Error(),"Create OWY taskbar child failed");
                if(!Native.AreDpiAwarenessContextsEqual(context,Native.GetWindowDpiAwarenessContext(strip))) { Hide("DPI contexts differ; safe parenting refused"); return; }
                Native.SetWindowLongPtr(strip,-16,0x40000000);
                Native.SetLastError(0); Native.SetParent(strip,taskbar);
                if(Native.GetParent(strip)!=taskbar) { Hide("Taskbar parenting failed: "+Marshal.GetLastWin32Error());return; }
                parent=taskbar;
                Native.SetLayeredWindowAttributes(strip,0x00ff00ff,255,1);
                if(hook!=0) Native.UnhookWinEvent(hook);
                Native.GetWindowThreadProcessId(taskbar,out var pid);
                hook=Native.SetWinEventHook(0x800B,0x800B,0,events,pid,0,0);
            }
            finally { Native.SetThreadDpiAwarenessContext(old); }
        }
        if(fontDpi!=dpi){if(font!=0)Native.DeleteObject(font);font=Native.CreateFont(-(int)Math.Round(12*scale),0,0,0,400,0,0,0,1,0,0,4,0,"Segoe UI");fontDpi=dpi;}
        int pad=Math.Max(2,(int)Math.Round(3*scale));
        int Measure(string text)
        {
            var dc=Native.GetDC(strip);var prev=Native.SelectObject(dc,font);var r=new Native.Rect();
            Native.DrawText(dc,text,text.Length,ref r,0x400|0x20|0x800);
            Native.SelectObject(dc,prev);Native.ReleaseDC(strip,dc);return r.Width+pad*2;
        }
        Native.Rect candidate=default;
        int count=showNetwork?2:showWatts?1:0;
        for(;count>=0;count--)
        {
            var text=Rows(count);int width=Math.Max(Measure(text[0]),Measure(text[1]));
            candidate=new Native.Rect { Left=trayRect.Left-width-pad,Top=bar.Top+1,Right=trayRect.Left-pad,Bottom=bar.Bottom-1 };
            if(candidate.Left>bar.Left+pad && !occupied.Any(r=>r.Intersects(candidate))) break;
        }
        if(count<0) { Hide("Taskbar crowded; readings available from tray");return; }
        var nextRows=Rows(count);bool boundsChanged=stripRect.ToString()!=candidate.ToString()||!Native.IsWindowVisible(strip);
        bool changed=!rows.SequenceEqual(nextRows)||boundsChanged;
        columns=count;rows=nextRows;stripRect=candidate;
        var pt=new Native.Point{X=candidate.Left,Y=candidate.Top};Native.ScreenToClient(taskbar,ref pt);
        if(boundsChanged && !Native.SetWindowPos(strip,0,pt.X,pt.Y,candidate.Width,candidate.Height,0x10|0x40)) { Hide("Cannot position taskbar window");return; }
        if(changed)Native.InvalidateRect(strip,0,true);
        SetReport(new(true,"Attached beside system tray",bar.ToString(),trayRect.ToString(),candidate.ToString(),dpi,count,occupied.Select(r=>r.ToString()).ToArray()));
    }
    void Hide(string message) { if(strip!=0) Native.ShowWindow(strip,0);SetReport(report with{Attached=false,Diagnostic=message}); }
    void SamplingChanged()
    {
        Native.SetTimer(owner,1,IsSamplingSuspended?30000u:2000u,0);
        SamplingSuspended?.Invoke(IsSamplingSuspended);
        if(!IsSamplingSuspended){geometryDirty=true;nextLayout=0;Place();}
    }
    void Paint(nint hwnd)
    {
        PaintCount++;
        var dc=Native.BeginPaint(hwnd,out var paint);
        Native.GetClientRect(hwnd,out var rect);
        var brush=Native.CreateSolidBrush(0xff00ff);Native.FillRect(dc,ref rect,brush);Native.DeleteObject(brush);
        var old=Native.SelectObject(dc,font);Native.SetBkMode(dc,1);
        bool light=(int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","SystemUsesLightTheme",0)==1;
        var contrast=new Native.HighContrast{Size=(uint)Marshal.SizeOf<Native.HighContrast>()};Native.SystemParametersInfo(0x42,contrast.Size,ref contrast,0);
        Native.SetTextColor(dc,(contrast.Flags&1)!=0?Native.GetSysColor(18):light?0u:0xffffffu);
        int lines=string.IsNullOrEmpty(rows[1])?1:2,line=(int)Math.Round(14*report.Dpi/96.0),top=(rect.Height-line*lines)/2;
        for(int i=0;i<lines;i++) { var r=new Native.Rect{Left=3,Top=top+i*line,Right=rect.Right-3,Bottom=top+(i+1)*line}; Native.DrawText(dc,rows[i],rows[i].Length,ref r,0x20|0x4|0x800); }
        Native.SelectObject(dc,old);Native.EndPaint(hwnd,ref paint);
    }
    nint Handle(nint hwnd,uint msg,nuint wp,nint lp)
    {
        if(msg==taskbarCreated && hwnd==owner) { parent=0;geometryDirty=true;AddTray();Place();return 0; }
        switch(msg)
        {
            case 0xF: if(hwnd==strip) { Paint(hwnd);return 0; }break;
            case 0x14: return 1;
            case 0x201: if(hwnd==strip) Clicked?.Invoke();return 0;
            case 0x8001: if((uint)lp==0x202 || (uint)lp==0x205) Clicked?.Invoke();return 0;
            case 0x8002: if(!IsSamplingSuspended && Environment.TickCount64>=nextLayout) { nextLayout=Environment.TickCount64+250;Place(); }return 0;
            case 0x113: if(hwnd==owner && wp==1 && !IsSamplingSuspended) Place();return 0;
            case 0x1A: case 0x7E: case 0x31A: geometryDirty=true;Place();if(strip!=0 && Native.IsWindow(strip))Native.InvalidateRect(strip,0,true);return 0;
            case 0x2B1:
                if(wp is 1 or 2 or 3 or 4 or 7 or 8){ReadSessionState();SamplingChanged();}return 0;
            case 0x218: if(wp is 4 or 6 or 7 or 18){sleeping=wp==4;if(!sleeping)ReadSessionState();SamplingChanged();}return 1;
            case 0x10: QuitRequested?.Invoke();return 0;
        }
        return Native.DefWindowProc(hwnd,msg,wp,lp);
    }
    public static void RunLoop() { while(Native.GetMessage(out var msg,0,0,0)>0) { Native.TranslateMessage(ref msg);Native.DispatchMessage(ref msg); } }
    public static void EndLoop() => Native.PostQuitMessage(0);
    public void RequestQuit() => Native.PostMessage(owner,0x10,0,0);
    public void Dispose()
    {
        if(disposed)return;disposed=true;Native.KillTimer(owner,1);Native.WTSUnRegisterSessionNotification(owner);Native.Shell_NotifyIcon(2,ref icon);
        if(hook!=0)Native.UnhookWinEvent(hook);if(Native.IsWindow(strip))Native.DestroyWindow(strip);Native.DestroyWindow(owner);if(font!=0)Native.DeleteObject(font);if(ownIcon!=0)Native.DestroyIcon(ownIcon);
    }
}
