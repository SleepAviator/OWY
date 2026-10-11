using System.Diagnostics;
using System.Text;

namespace OWY.Core;

public sealed class CodexClient : IAsyncDisposable
{
    readonly string directory;
    readonly Func<CodexReading,ValueTask> publish;
    readonly CancellationTokenSource stop=new();
    Process? process;
    Task? run;
    public int ProcessId=>process is {HasExited:false}?process.Id:0;
    public string? LastIssue {get;private set;}
    public bool EcoQoSApplied {get;private set;}
    public CodexClient(string directory,Func<CodexReading,ValueTask> publish){this.directory=directory;this.publish=publish;}
    public void Start()=>run=Task.Run(Run);
    async Task Run()
    {
        while(!stop.IsCancellationRequested)
        {
            Task? errors=null;string? stderr=null;
            try
            {
                var info=new ProcessStartInfo(Path.Combine(directory,"python","python.exe")) {UseShellExecute=false,CreateNoWindow=true,
                    RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=directory};
                info.ArgumentList.Add("-B");info.ArgumentList.Add("-u");info.ArgumentList.Add(Path.Combine(directory,"worker","owy_worker.py"));
                info.Environment["PYTHONDONTWRITEBYTECODE"]="1";
                process=Process.Start(info)??throw new IOException("Codex worker did not start");
                EcoQoSApplied=false;EcoQoS.EnableProcess(process);EcoQoSApplied=true;
                var stdout=new BoundedLineReader(process.StandardOutput);var errorLines=new BoundedLineReader(process.StandardError,4096);
                errors=Task.Run(async()=>{string? line;while((line=await errorLines.Read(stop.Token)) is not null)stderr=line[..Math.Min(line.Length,240)];});
                long sequence=0;
                while(await stdout.Read(stop.Token) is {} line)
                {
                    CodexReading reading;
                    using(EcoQoS.EnterThread())
                    {
                        if(Encoding.UTF8.GetByteCount(line)>Protocol.MaxFrameBytes)throw new InvalidDataException("IPC frame exceeded byte bound");
                        reading=Protocol.ParseCodex(line);
                        if(reading.Sequence<=sequence)throw new InvalidDataException("Worker sequence moved backwards");
                        sequence=reading.Sequence;
                    }
                    await publish(reading);
                }
                if(!stop.IsCancellationRequested)throw new IOException("Codex worker stopped"+(stderr is null?"":": "+stderr));
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}
            catch(Exception ex) when(ex is IOException or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {LastIssue=ex.Message;await publish(CodexReading.Unavailable("Codex worker: "+ex.Message));}
            finally
            {
                if(process is not null)
                {
                    await StopProcess(process);
                }
                if(errors is not null)try{await errors;}catch(OperationCanceledException) when(stop.IsCancellationRequested){}catch(IOException ex){LastIssue=ex.Message;}
                process?.Dispose();process=null;
            }
            try{await Task.Delay(5000,stop.Token);}catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}
        }
    }
    static async Task StopProcess(Process child)
    {
        if(child.HasExited)return;
        try{await child.StandardInput.WriteLineAsync("quit");child.StandardInput.Close();}
        catch(IOException) when(child.HasExited){}
        using var timeout=new CancellationTokenSource(8000);
        try{await child.WaitForExitAsync(timeout.Token);}
        catch(OperationCanceledException){child.Kill(true);await child.WaitForExitAsync();}
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();try{if(run is not null)await run;}finally{stop.Dispose();}
    }
}
