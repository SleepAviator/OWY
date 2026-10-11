using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OWY.Core;

/// <summary>EcoQoS for background processes and synchronous work on pooled threads.</summary>
public static class EcoQoS
{
    const uint ExecutionSpeed=1;
    const int ProcessPowerThrottling=4,ThreadPowerThrottling=3;
    [StructLayout(LayoutKind.Sequential)]
    struct State {public uint Version,ControlMask,StateMask;}

    public static void EnableProcess(Process process)
    {
        var state=new State{Version=1};
        if(!GetProcessInformation(process.Handle,ProcessPowerThrottling,ref state,12))
            throw Failure("read process EcoQoS");
        state.ControlMask|=ExecutionSpeed;state.StateMask|=ExecutionSpeed;
        if(!SetProcessInformation(process.Handle,ProcessPowerThrottling,ref state,12))
            throw Failure("enable process EcoQoS");
    }

    // A scope must finish on its original native thread, before any await.
    // Restore the previous policy so pool threads cannot carry EcoQoS into UI work.
    public static ThreadScope EnterThread()=>new();
    public sealed class ThreadScope : IDisposable
    {
        readonly State previous;
        readonly uint threadId;
        bool disposed;
        internal ThreadScope()
        {
            threadId=GetCurrentThreadId();var state=new State{Version=1};
            if(!GetThreadInformation(GetCurrentThread(),ThreadPowerThrottling,ref state,12))
                throw Failure("read thread EcoQoS");
            previous=state;state.ControlMask|=ExecutionSpeed;state.StateMask|=ExecutionSpeed;
            if(!SetThreadInformation(GetCurrentThread(),ThreadPowerThrottling,ref state,12))
                throw Failure("enable thread EcoQoS");
        }
        public void Dispose()
        {
            if(disposed)return;
            if(GetCurrentThreadId()!=threadId)throw new InvalidOperationException("An EcoQoS thread scope cannot cross an await or change threads");
            var state=previous;
            if(!SetThreadInformation(GetCurrentThread(),ThreadPowerThrottling,ref state,12))
                throw Failure("restore thread EcoQoS");
            disposed=true;
        }
    }
    static Win32Exception Failure(string operation)=>new(Marshal.GetLastWin32Error(),"Cannot "+operation);
    [DllImport("kernel32.dll")]static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessInformation(nint process,int kind,ref State state,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetProcessInformation(nint process,int kind,ref State state,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetThreadInformation(nint thread,int kind,ref State state,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetThreadInformation(nint thread,int kind,ref State state,uint size);
}
