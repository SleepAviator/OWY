using System.Threading.Channels;

namespace OWY.Core;

/// <summary>One accounting consumer. Display updates may coalesce only after this queue.</summary>
public sealed class MonitorEngine : IAsyncDisposable
{
    sealed record Query(int Range,int Count,TaskCompletionSource<HistoryView> Result);
    readonly Channel<object> queue=Channel.CreateBounded<object>(new BoundedChannelOptions(128){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    readonly History history;
    readonly Task consumer;
    MonitorReading latest=new(DateTimeOffset.UtcNow,CodexReading.Unavailable("Starting"),HardwareReading.Empty,[]);
    public MonitorReading Latest=>Volatile.Read(ref latest);
    public long JournalBytes=>history.BytesWritten;
    public int JournalFlushes=>history.FlushCount;
    public string? HistoryIssue=>history.Issue;
    public event Action? Changed;
    public event Action<Exception>? Failed;
    public MonitorEngine(string directory){history=new(directory,DateTimeOffset.UtcNow);consumer=Task.Run(Consume);}
    public ValueTask Publish(CodexReading reading)=>queue.Writer.WriteAsync(reading);
    public ValueTask Publish(HardwareReading reading)=>queue.Writer.WriteAsync(reading);
    public async Task<HistoryView> View(int range,int count)
    {
        var result=new TaskCompletionSource<HistoryView>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new Query(range,count,result));return await result.Task;
    }
    async Task Consume()
    {
        try
        {
        await foreach(var item in queue.Reader.ReadAllAsync())
        {
            using(EcoQoS.EnterThread())
            {
                if(item is Query query)
                {try{query.Result.SetResult(history.View(query.Range,DateTimeOffset.UtcNow,query.Count));}catch(Exception ex){query.Result.SetException(ex);}continue;}
                var previous=Latest;var now=DateTimeOffset.UtcNow;
                var c=item as CodexReading??previous.Codex;var h=item as HardwareReading??previous.Hardware;
                // Receipt corrections are always accounted before any UI notification.
                var value=new MonitorReading(now,c,h,history.Issue is {} issue?[issue]:[]);
                history.Add(value);Volatile.Write(ref latest,value);
            }
            Changed?.Invoke();
        }
        }
        catch(Exception ex)
        {
            // Release blocked producers and queries before reporting the fatal error.
            // The consumer remains faulted so shutdown cannot mistake this for success.
            queue.Writer.TryComplete(ex);
            while(queue.Reader.TryRead(out var item))if(item is Query query)query.Result.TrySetException(ex);
            Failed?.Invoke(ex);throw;
        }
    }
    public async ValueTask DisposeAsync(){queue.Writer.TryComplete();try{await consumer;}finally{history.Dispose();}}
}
