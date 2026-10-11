using System.Text;
using System.Text.Json;

namespace OWY.Core;

public sealed record FineSample(long Second, double? TotalTps, double? AverageTps, double? Cpu,
    double? Memory, double? Upload, double? Download, double? SystemWatts, bool RateCovered);
public sealed record MinuteRecord(int Version,long Minute,int Samples,int RateSamples,double RateSum,double AverageSum,
    int AverageSamples,long OutputTokens,int ObservedSeconds,int PartialSeconds,double CpuSum,int CpuSamples,
    double MemorySum,int MemorySamples,double UploadSum,double DownloadSum,int NetworkSamples,double WattsSum,int WattsSamples);
public sealed record ChartBin(long Start,long End,double? TotalTps,double? AverageTps,long OutputTokens,double RateCoverage,double ReceiptCoverage,double ReceiptObservedCoverage=0)
{
    // A known receipt remains visible under partial coverage. Missing receipt
    // observations cannot become idle zeroes from a generation speed sample.
    public double? ReceivedTps=>OutputTokens>0 || ReceiptObservedCoverage>0?OutputTokens/(double)(End-Start):null;
}
public sealed record HistoryView(int RangeSeconds,ChartBin[] Bins,long OutputTokens,double Coverage);

/// <summary>Single-owner accounting. Call before publishing/coalescing UI frames.</summary>
public sealed class History : IDisposable
{
    public static readonly int[] Ranges=[600,3600,10800,21600,43200,86400,259200,604800,1209600,2419200];
    readonly string directory;
    readonly SortedDictionary<long,MinuteRecord> minutes=[];
    readonly SortedDictionary<long,FineSample> fine=[];
    readonly Dictionary<long,ReceiptChange> receipts=[];
    readonly HashSet<long> dirty=[];
    DateTimeOffset lastFlush;
    long lastSecond=long.MinValue;
    public long BytesWritten {get;private set;}
    public int FlushCount {get;private set;}
    public string? Issue {get;private set;}
    public History(string directory,DateTimeOffset now)
    {
        this.directory=directory;lastFlush=now;Directory.CreateDirectory(directory);
        foreach(var file in Directory.EnumerateFiles(directory,"????-??-??.jsonl").Order())
        {
            if(!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file),"yyyy-MM-dd",null,System.Globalization.DateTimeStyles.None,out var day))continue;
            if(day.Date<now.UtcDateTime.Date.AddDays(-28)){File.Delete(file);continue;}
            // Read committed newline-terminated records only. A partial final
            // record is removed once, from this app's own journal, before append.
            byte[] bytes=File.ReadAllBytes(file);
            int end=Array.LastIndexOf(bytes,(byte)'\n')+1;
            if(end<bytes.Length)
            {
                using var repair=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.Read);
                repair.SetLength(end);Issue="Recovered an interrupted journal record";
            }
            foreach(var line in Encoding.UTF8.GetString(bytes,0,end).Split('\n',StringSplitOptions.RemoveEmptyEntries))
            {
                MinuteRecord? record;
                try{record=JsonSerializer.Deserialize<MinuteRecord>(line,Protocol.Json);}
                catch(JsonException){Issue="A malformed journal record was excluded";continue;}
                if(record is null || !Valid(record))
                {Issue="An unsupported journal record was excluded";continue;}
                if(record.Minute>=now.AddDays(-28).ToUnixTimeSeconds())minutes[record.Minute]=record;
            }
        }
    }
    static MinuteRecord Empty(long minute)=>new(1,minute,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0);
    static bool Valid(MinuteRecord r)=>r.Version==1 && r.Minute>=0 && r.Minute%60==0 && r.Samples is >=0 and <=60 && r.OutputTokens>=0
        && new[]{r.RateSamples,r.AverageSamples,r.CpuSamples,r.MemorySamples,r.NetworkSamples,r.WattsSamples}.All(n=>n>=0 && n<=r.Samples)
        && r.ObservedSeconds is >=0 and <=60 && r.PartialSeconds is >=0 and <=60
        && new[]{r.RateSum,r.AverageSum,r.CpuSum,r.MemorySum,r.UploadSum,r.DownloadSum,r.WattsSum}.All(n=>double.IsFinite(n) && n>=0);
    public void Add(MonitorReading value)
    {
        long second=value.SampledAt.ToUnixTimeSeconds(),minute=second/60*60;
        foreach(var receipt in value.Codex.ReceiptChanges)
        {
            if(receipt.Second<second-600 || receipt.Second>second+1)continue;
            var old=receipts.GetValueOrDefault(receipt.Second);
            if(old==receipt)continue;
            receipts[receipt.Second]=receipt;
            long m=receipt.Second/60*60;
            var row=minutes.GetValueOrDefault(m)??Empty(m);
            minutes[m]=row with{OutputTokens=Math.Max(0,row.OutputTokens+receipt.OutputTokens-(old?.OutputTokens??0)),
                ObservedSeconds=Math.Clamp(row.ObservedSeconds+(receipt.Observed?1:0)-(old?.Observed==true?1:0),0,60),
                PartialSeconds=Math.Clamp(row.PartialSeconds+(receipt.Partial?1:0)-(old?.Partial==true?1:0),0,60)};
            dirty.Add(m);
        }
        if(second>lastSecond)
        {
            var c=value.Codex;var h=value.Hardware;
            bool fresh=second-c.SampledAt is >=-1 and <3 && c.Coverage is "complete" or "partial";
            double? total=fresh?c.TotalTps:null,avg=fresh?c.AverageTps:null;
            var row=minutes.GetValueOrDefault(minute)??Empty(minute);
            if(row.Samples<60)
                minutes[minute]=row with{Samples=row.Samples+1,RateSamples=row.RateSamples+(total.HasValue?1:0),
                    RateSum=row.RateSum+(total??0),AverageSum=row.AverageSum+(avg??0),AverageSamples=row.AverageSamples+(avg.HasValue?1:0),
                    CpuSum=row.CpuSum+(h.CpuPercent??0),CpuSamples=row.CpuSamples+(h.CpuPercent.HasValue?1:0),
                    MemorySum=row.MemorySum+(h.MemoryPercent??0),MemorySamples=row.MemorySamples+(h.MemoryPercent.HasValue?1:0),
                    UploadSum=row.UploadSum+(h.UploadBytesPerSecond??0),DownloadSum=row.DownloadSum+(h.DownloadBytesPerSecond??0),NetworkSamples=row.NetworkSamples+(h.UploadBytesPerSecond.HasValue?1:0),
                    WattsSum=row.WattsSum+(h.SystemWatts??0),WattsSamples=row.WattsSamples+(h.SystemWatts.HasValue?1:0)};
            fine[second]=new(second,total,avg,h.CpuPercent,h.MemoryPercent,h.UploadBytesPerSecond,h.DownloadBytesPerSecond,h.SystemWatts,c.Coverage=="complete" && total.HasValue);
            dirty.Add(minute);lastSecond=second;
        }
        foreach(long old in fine.Keys.TakeWhile(x=>x<second-600).ToArray())fine.Remove(old);
        foreach(long old in receipts.Keys.Where(x=>x<second-600).ToArray())receipts.Remove(old);
        foreach(long old in minutes.Keys.TakeWhile(x=>x<second-28*86400).ToArray()){minutes.Remove(old);dirty.Remove(old);}
        if(value.SampledAt-lastFlush>=TimeSpan.FromMinutes(15))Flush(value.SampledAt,false);
    }
    public HistoryView View(int range,DateTimeOffset now,int count=120)
    {
        if(!Ranges.Contains(range))throw new ArgumentOutOfRangeException(nameof(range));
        if(count<1 || count>600)throw new ArgumentOutOfRangeException(nameof(count));
        long end=now.ToUnixTimeSeconds(),start=end-range;
        var bins=new ChartBin[count];long sum=0;double coverage=0,cumulativeOutput=0;
        var rates=new double[count];var averages=new double[count];var rateCounts=new double[count];var averageCounts=new double[count];
        var outputs=new double[count];var observations=new double[count];var receiptObserved=new double[count];
        void Allocate(long left,long right,double rate,double avg,double rateN,double avgN,double output,double covered,double observed)
        {
            if(right<=start || left>=end || right<=left)return;
            int lo=Math.Clamp((int)((left-start)*count/range),0,count-1),hi=Math.Clamp((int)((right-start)*count/range),0,count-1);
            for(int i=lo;i<=hi;i++)
            {
                long b0=start+(long)range*i/count,b1=start+(long)range*(i+1)/count;
                double weight=Math.Max(0,Math.Min(right,b1)-Math.Max(left,b0))/(double)(right-left);
                rates[i]+=rate*weight;averages[i]+=avg*weight;rateCounts[i]+=rateN*weight;averageCounts[i]+=avgN*weight;
                outputs[i]+=output*weight;observations[i]+=covered*weight;receiptObserved[i]+=observed*weight;
            }
        }
        bool detailed=range==600 && fine.Count>0;long first=detailed?fine.Keys.Min():long.MaxValue;
        var fineGroups=detailed?fine.Values.GroupBy(x=>x.Second/60*60).ToDictionary(x=>x.Key,x=>x.ToArray()):[];
        var receiptGroups=detailed?receipts.Values.GroupBy(x=>x.Second/60*60).ToDictionary(x=>x.Key,x=>x.ToArray()):[];
        foreach(var m in minutes.Values.Where(x=>x.Minute+60>start && x.Minute<end))
        {
            if(!detailed){Allocate(m.Minute,m.Minute+60,m.RateSum,m.AverageSum,m.RateSamples,m.AverageSamples,m.OutputTokens,Math.Max(0,m.ObservedSeconds-m.PartialSeconds),m.ObservedSeconds);continue;}
            if(m.Minute>=first)continue;
            var fs=fineGroups.GetValueOrDefault(m.Minute)??[];var rs=receiptGroups.GetValueOrDefault(m.Minute)??[];
            long right=Math.Min(first,m.Minute+60);
            Allocate(m.Minute,right,Math.Max(0,m.RateSum-fs.Sum(x=>x.TotalTps??0)),Math.Max(0,m.AverageSum-fs.Sum(x=>x.AverageTps??0)),
                Math.Max(0,m.RateSamples-fs.Count(x=>x.TotalTps.HasValue)),Math.Max(0,m.AverageSamples-fs.Count(x=>x.AverageTps.HasValue)),
                Math.Max(0,m.OutputTokens-rs.Sum(x=>x.OutputTokens)),Math.Max(0,m.ObservedSeconds-m.PartialSeconds-rs.Count(x=>x.Observed && !x.Partial)),
                Math.Max(0,m.ObservedSeconds-rs.Count(x=>x.Observed)));
        }
        if(detailed)
        {
            foreach(var s in fine.Values)Allocate(s.Second,s.Second+1,s.TotalTps??0,s.AverageTps??0,s.TotalTps.HasValue?1:0,s.AverageTps.HasValue?1:0,0,0,0);
            foreach(var r in receipts.Values)Allocate(r.Second,r.Second+1,0,0,0,0,r.OutputTokens,r.Observed && !r.Partial?1:0,r.Observed?1:0);
        }
        for(int i=0;i<count;i++)
        {
            long left=start+(long)range*i/count,right=start+(long)range*(i+1)/count;
            long before=(long)Math.Round(cumulativeOutput);cumulativeOutput+=outputs[i];
            long tokens=(long)Math.Round(cumulativeOutput)-before;sum+=tokens;coverage+=Math.Min(1,observations[i]/(right-left));
            bins[i]=new(left,right,rateCounts[i]>0?rates[i]/rateCounts[i]:null,averageCounts[i]>0?averages[i]/averageCounts[i]:null,tokens,Math.Min(1,rateCounts[i]/(right-left)),Math.Min(1,observations[i]/(right-left)),Math.Min(1,receiptObserved[i]/(right-left)));
        }
        return new(range,bins,sum,coverage/count);
    }
    public void Flush(DateTimeOffset now,bool includeCurrent)
    {
        long current=now.ToUnixTimeSeconds()/60*60;
        foreach(var group in dirty.Where(x=>includeCurrent || x<current).Order().GroupBy(x=>DateTimeOffset.FromUnixTimeSeconds(x).UtcDateTime.ToString("yyyy-MM-dd")))
        {
            string path=Path.Combine(directory,group.Key+".jsonl");
            using var file=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read,4096);
            foreach(long key in group)
            {
                byte[] row=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(minutes[key],Protocol.Json)+"\n");
                file.Write(row);BytesWritten+=row.Length;
            }
            file.Flush(true);
            foreach(long key in group)dirty.Remove(key);
        }
        // Retention cleanup runs per flush, not once a second.
        foreach(var file in Directory.EnumerateFiles(directory,"????-??-??.jsonl"))
            if(DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file),"yyyy-MM-dd",null,System.Globalization.DateTimeStyles.None,out var d) && d.Date<now.UtcDateTime.Date.AddDays(-28))File.Delete(file);
        FlushCount++;lastFlush=now;
    }
    public void Dispose()=>Flush(DateTimeOffset.UtcNow,true);
}
