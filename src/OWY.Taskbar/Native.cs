using System.Runtime.InteropServices;

namespace OWY.Taskbar;

internal static class Native
{
    internal delegate nint WindowProc(nint hwnd, uint msg, nuint wp, nint lp);
    internal delegate void WinEventProc(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Intersects(Rect b) => Left < b.Right && Right > b.Left && Top < b.Bottom && Bottom > b.Top;
        public override readonly string ToString() => $"{Left},{Top},{Right},{Bottom}";
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Proc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu;
        public string Name;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Hwnd; public uint Msg; public nuint Wp; public nint Lp; public uint Time; public Point Pt; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] internal struct Paint { public nint DC; public int Erase; public Rect Area; public int Restore, Update; [MarshalAs(UnmanagedType.ByValArray, SizeConst=32)] public byte[] Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] internal struct NotifyIcon
    {
        public uint Size; public nint Hwnd; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern nint FindWindow(string cls,string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent,nint after,string cls,string? name);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd,out Rect r);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd,out Rect r);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint hwnd,ref Point p);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern bool AreDpiAwarenessContextsEqual(nint a,nint b);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll",SetLastError=true)] internal static extern nint SetParent(nint hwnd,nint parent);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)] internal static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("kernel32.dll")] internal static extern void SetLastError(uint error);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd,int show);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint hwnd,nint rect,bool erase);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint hwnd,out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint hwnd,ref Paint paint);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern nint DefWindowProc(nint hwnd,uint msg,nuint wp,nint lp);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd,uint msg,nuint wp,nint lp);
    [DllImport("user32.dll")] internal static extern nint SendMessage(nint hwnd,uint msg,nuint wp,nint lp);
    [DllImport("user32.dll")] internal static extern int GetMessage(out Message msg,nint hwnd,uint min,uint max);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message msg);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint hwnd,nuint id,uint interval,nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint hwnd,nuint id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int DrawText(nint dc,string text,int length,ref Rect r,uint format);
    [DllImport("user32.dll")] internal static extern nint LoadCursor(nint instance,nint id);
    [DllImport("user32.dll")] internal static extern nint LoadIcon(nint instance,nint id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint LoadImage(nint instance,string name,uint type,int cx,int cy,uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(nint hwnd,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min,uint max,nint module,WinEventProc callback,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] internal static extern nint CreateFont(int height,int width,int escape,int orientation,int weight,uint italic,uint underline,uint strike,uint charset,uint outPrecision,uint clipPrecision,uint quality,uint family,string face);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(nint dc,int mode);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(nint dc,uint color);
    [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll")] internal static extern int FillRect(nint dc,ref Rect rect,nint brush);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] internal static extern bool Shell_NotifyIcon(uint message,ref NotifyIcon data);
    [StructLayout(LayoutKind.Sequential)] internal struct HighContrast{public uint Size,Flags;public nint Scheme;}
    [DllImport("user32.dll")]internal static extern bool SystemParametersInfo(uint action,uint param,ref HighContrast data,uint flags);
    [DllImport("user32.dll")]internal static extern uint GetSysColor(int index);
    [DllImport("wtsapi32.dll",SetLastError=true)]internal static extern bool WTSRegisterSessionNotification(nint window,uint flags);
    [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)]internal static extern bool WTSQuerySessionInformation(nint server,uint session,int info,out nint buffer,out uint bytes);
    [DllImport("wtsapi32.dll")]internal static extern void WTSFreeMemory(nint buffer);
    [DllImport("wtsapi32.dll")]internal static extern bool WTSUnRegisterSessionNotification(nint window);
}
