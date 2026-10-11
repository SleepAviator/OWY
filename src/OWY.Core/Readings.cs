using System.Text.Json;
using System.Text.Json.Serialization;

namespace OWY.Core;

public sealed record ReceiptChange(long Second, long OutputTokens, bool Observed, bool Partial);
public sealed record SessionReading(double Tps, string Provider, string Model);
public sealed record CodexReading(int Version, long Sequence, double SampledAt, double? TotalTps,
    double? AverageTps, int ContributingSessions,
    [property:JsonPropertyName("pace_300_tps")] double? Pace300Tps,
    [property:JsonPropertyName("pace_300_tokens")] long Pace300Tokens, bool PacePartial,
    string Coverage, string[] Issues, int PendingCount, SessionReading[] Sessions, ReceiptChange[] ReceiptChanges)
{
    public double? TelemetryReceivedAt {get;init;}
    public long TelemetryBatches {get;init;}
    public static CodexReading Unavailable(string issue) => new(1,0,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0,null,null,0,null,0,true,"unavailable",[issue],0,[],[]);
}
public sealed record SensorValue(string Id, string Name, double? Value, string Unit, DateTimeOffset SampledAt, string Status, string Basis);
public sealed record HardwareReading(DateTimeOffset SampledAt, double? CpuPercent, double? MemoryPercent,
    long? MemoryUsedBytes, long? MemoryTotalBytes, double? UploadBytesPerSecond, double? DownloadBytesPerSecond,
    double? DiskBytesPerSecond, double? GpuPercent, double? BatteryPercent, bool OnBattery, bool BatterySaver,
    double? SystemWatts, string PowerBasis, SensorValue[] Details, string[] Issues)
{
    public double? ComponentWatts {get;init;}
    public string ComponentPowerBasis {get;init;}="unavailable";
    public string ActiveGpu {get;init;}="Unavailable";
    public string ActiveGpuKind {get;init;}="unavailable";
    public double? ActiveGpuWatts {get;init;}
    public string GpuSelectionBasis {get;init;}="GPU activity unavailable";
    public static HardwareReading Empty => new(DateTimeOffset.MinValue,null,null,null,null,null,null,null,null,null,false,false,null,"unavailable",[],[]);
}
public sealed record MonitorReading(DateTimeOffset SampledAt, CodexReading Codex, HardwareReading Hardware, string[] Issues);

public static class Protocol
{
    public const int Version=1;
    public const int MaxFrameBytes=128*1024;
    public static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,NumberHandling=JsonNumberHandling.Strict};
    public static CodexReading ParseCodex(string text)
    {
        if(text.Length>MaxFrameBytes)throw new InvalidDataException("Codex worker frame exceeds IPC bound");
        var r=JsonSerializer.Deserialize<CodexReading>(text,Json) ?? throw new InvalidDataException("Empty Codex frame");
        if(r.Version!=1 || r.Sequence<=0 || !double.IsFinite(r.SampledAt) || r.ContributingSessions<0
            || r.ReceiptChanges is null || r.ReceiptChanges.Length>2048 || r.Sessions is null || r.Sessions.Length>512
            || r.Issues is null || r.Issues.Length>256 || r.Coverage is not ("complete" or "partial" or "unavailable" or "initializing"))
            throw new InvalidDataException("Invalid Codex snapshot");
        foreach(var value in new[]{r.TotalTps,r.AverageTps,r.Pace300Tps})
            if(value is {} n && (!double.IsFinite(n)||n<0))throw new InvalidDataException("Invalid Codex rate");
        if(r.Pace300Tokens<0 || r.PendingCount<0 || r.Issues.Any(x=>x is null || x.Length>4096))throw new InvalidDataException("Invalid Codex coverage");
        if(r.TelemetryBatches<0 || r.TelemetryReceivedAt is {} received && (!double.IsFinite(received) || received<0 || received>r.SampledAt+1))throw new InvalidDataException("Invalid telemetry freshness");
        if(r.ReceiptChanges.Any(x=>x is null || x.OutputTokens<0 || x.Second<0 || x.Second>r.SampledAt+1))throw new InvalidDataException("Invalid output receipt");
        if(r.Sessions.Any(x=>x is null || !double.IsFinite(x.Tps)||x.Tps<0 || x.Provider is null || x.Model is null || x.Provider.Length>200 || x.Model.Length>200))throw new InvalidDataException("Invalid session projection");
        return r;
    }
}

public static class Display
{
    public static string Rate(double? value) => value is {} n ? (n>=100?n.ToString("0"):n.ToString("0.#"))+" t/s" : "— t/s";
    public static string Bytes(double? value) => value switch {null=>"—",>=1e9=>(value/1e9)?.ToString("0.#")+" GB/s",>=1e6=>(value/1e6)?.ToString("0.#")+" MB/s",>=1e3=>(value/1e3)?.ToString("0.#")+" KB/s",_=>value?.ToString("0")+" B/s"};
    public static string Tokens(long value) => value>=1_000_000 ? (value/1e6).ToString("0.#")+"M" : value>=1000?(value/1e3).ToString("0.#")+"K":value.ToString();
    public static string Watts(double? value) => value is {} w?w.ToString("0.#")+" W":"— W";
    public static string Power(HardwareReading reading) => reading.SystemWatts.HasValue?Watts(reading.SystemWatts):
        reading.ComponentWatts.HasValue?reading.ComponentPowerBasis+" "+Watts(reading.ComponentWatts):Watts(null);
    public static string Size(double value) => value>=1L<<30?(value/(1L<<30)).ToString("0.#")+" GiB":value>=1<<20?(value/(1<<20)).ToString("0.#")+" MiB":value>=1024?(value/1024).ToString("0.#")+" KiB":value.ToString("0")+" B";
}
