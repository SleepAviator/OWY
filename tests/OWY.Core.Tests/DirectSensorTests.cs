using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using OWY.Core;
using OWY.Hardware;

static class DirectSensorTests
{
    static int checks;
    static void Check(bool valid,string name){if(!valid)throw new InvalidOperationException(name);checks++;}
    static void Invalid(Action action,string name){try{action();}catch(InvalidDataException){checks++;return;}throw new InvalidOperationException(name);}
    static void Set(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
    static void Crc(byte[] bytes){bytes[89]=0;for(int i=3;i<89;i++)bytes[89]^=bytes[i];}
    public static void Run()
    {
        var reply=BladeFanSensors.Request(1);reply[1]=2;reply[11]=24;Crc(reply);
        Check(BladeFanSensors.Parse(reply,1)==2400,"Actual fan RPM");
        Invalid(()=>BladeFanSensors.Parse(reply,2),"Fan zone mismatch");
        reply[8]=0x81;Crc(reply);Invalid(()=>BladeFanSensors.Parse(reply,1),"Setpoint never reported as RPM");
        reply[8]=0x88;reply[11]=0;Crc(reply);Check(BladeFanSensors.Parse(reply,1)==0,"Fan stop stays zero");
        reply[11]=255;Crc(reply);Invalid(()=>BladeFanSensors.Parse(reply,1),"Invalid fan RPM");
        reply[11]=24;Crc(reply);reply[89]^=1;Invalid(()=>BladeFanSensors.Parse(reply,1),"Bad fan checksum");
        reply[1]=1;Crc(reply);Invalid(()=>BladeFanSensors.Parse(reply,1),"Busy fan response unavailable");
        using(var denied=new BladeFanSensors()){denied.Sample(()=>false);Check(!denied.Connected && denied.Exchanges==0,"Denied fan read does no device work");}
        var now=DateTimeOffset.UtcNow;byte[] nvme=new byte[560];
        var amd=AmdReading.FromMetrics("fixture",new Dictionary<int,int>{{23,14},{30,0},{33,10},{34,3000},{71,1147},{72,21},{38,25},{39,10}},now);
        Check(amd.Sensors.Single(x=>x.Id=="amd:npu:clock").Value==1147,"NPU clock uses supported ID 71");
        Check(amd.Sensors.Single(x=>x.Id=="amd:npu:load").Value==21,"NPU busy uses supported ID 72");
        Check(amd.PackageWatts==14 && amd.GraphicsWatts==0,"CPU/GFX subdomains do not inflate the package");
        Check(amd.Sensors.Count(x=>x.Unit=="MHz" && x.Value.HasValue)==2,"PowerShift IDs 38/39 are not video clocks");
        Check(!AmdReading.FromMetrics("fixture",new Dictionary<int,int>{{72,101},{71,-1}},now).Sensors.Any(x=>x.Id.StartsWith("amd:npu:")),"Invalid/unsupported extra metrics omitted");
        Set(nvme,0,48);Set(nvme,4,48);Set(nvme,8,3);Set(nvme,12,2);Set(nvme,24,40);Set(nvme,28,512);
        BinaryPrimitives.WriteUInt16LittleEndian(nvme.AsSpan(49,2),327);nvme[51]=100;nvme[53]=5;
        BinaryPrimitives.WriteUInt16LittleEndian(nvme.AsSpan(248,2),327);
        BinaryPrimitives.WriteUInt16LittleEndian(nvme.AsSpan(250,2),307);
        nvme[96]=2;nvme[48+112]=18;
        var readings=NvmeSensors.Parse(nvme,1,now);
        Check(Math.Abs(readings.Single(x=>x.Id=="nvme:1:temperature").Value!.Value-53.85)<.001,"NVMe Kelvin conversion");
        Check(readings.Count(x=>x.Unit=="°C")==2,"Duplicate NVMe temperature omitted");
        Check(readings.Single(x=>x.Id=="nvme:1:write").Value==1024000,"NVMe data units count 512000 bytes");
        nvme[96+8]=1;Check(NvmeSensors.Parse(nvme,1,now).Single(x=>x.Id=="nvme:1:write").Value>ulong.MaxValue,"NVMe counters retain upper 128-bit words");
        Set(nvme,24,uint.MaxValue);Invalid(()=>NvmeSensors.Parse(nvme,1,now),"NVMe malicious offset");
        Set(nvme,24,40);Invalid(()=>NvmeSensors.Parse(nvme[..559],1,now),"Truncated NVMe log");
        Set(nvme,12,1);Invalid(()=>NvmeSensors.Parse(nvme,1,now),"NVMe data type mismatch");
        byte[] info=new byte[36],status=new byte[16];Set(info,0,0x80000000);Set(info,12,68000);Set(info,16,66000);Set(info,32,18);
        Set(status,0,2);Set(status,4,40000);Set(status,8,16694);Set(status,12,unchecked((uint)-10000));
        var battery=BatterySensors.Parse(info,status,now);
        Check(battery.Single(x=>x.Id=="battery:discharge").Value==10,"Battery driver discharge mW to W");
        Check(battery.Single(x=>x.Id=="battery:voltage").Value==16.694,"Battery voltage mV to V");
        Check(Math.Abs(battery.Single(x=>x.Id=="battery:wear").Value!.Value-100*2d/68)<.0001,"Battery wear");
        Set(status,0,1);Check(BatterySensors.Parse(info,status,now).Single(x=>x.Id=="battery:discharge").Value is null,"AC rate is not system watts");
        Set(status,0,6);Check(BatterySensors.Parse(info,status,now).Single(x=>x.Id=="battery:discharge").Value is null,"Charging is not discharge");
        Set(info,0,0xc0000000);Check(BatterySensors.Parse(info,status,now).Where(x=>x.Unit is "Wh" or "W" or "%").All(x=>x.Value is null),"Relative capacity is not Wh/watts");
        Invalid(()=>BatterySensors.Parse(info[..35],status,now),"Truncated battery reply");
        byte[] thermal=new byte[168],voltage=new byte[76];Set(thermal,0,168|2u<<16);Set(thermal,4,4);Set(thermal,48,54*256);Set(voltage,0,76|1u<<16);Set(voltage,40,720000);
        var extra=NvidiaExtraSensors.Parse(thermal,voltage,now);
        Check(extra[0].Value==54 && extra[1].Value==.72,"RTX 50 memory junction and microvolts conversion");
        Set(thermal,48,255*256);Set(voltage,40,0);Check(NvidiaExtraSensors.Parse(thermal,voltage,now).All(s=>s.Value is null),"GPU thermal sentinels/initial zero voltage unavailable");
        Invalid(()=>NvidiaExtraSensors.Parse(thermal[..167],voltage,now),"Truncated NVAPI reply");
        using(var denied=new NvidiaExtraSensors(()=>false))
        {
            denied.Sample(new("discrete","NVIDIA GeForce RTX 5070 Laptop GPU",1,true,"fixture"),now);
            Check(!denied.Initialized && denied.Queries==0 && denied.Connections==0,"Denied NVAPI guard never loads or queries the driver");
        }
        Console.WriteLine($"{checks} direct-device protocol and accounting checks passed");
    }
    public static async Task Live(string[] args)
    {
        int at=Array.IndexOf(args,"--output");string path=at>=0?args[at+1]:"artifacts/validation/direct-sensors-live.json";
        var samples=new List<object>();using var fans=new BladeFanSensors();using var disk=new NvmeSensors();using var battery=new BatterySensors();
        using var process=Process.GetCurrentProcess();double before=process.TotalProcessorTime.TotalMilliseconds;var wall=Stopwatch.StartNew();
        for(int i=0;i<3;i++)
        {
            using(EcoQoS.EnterThread())
            {
                var query=Stopwatch.StartNew();var f=fans.Sample(()=>true);double fanMs=query.Elapsed.TotalMilliseconds;
                query.Restart();var d=disk.Sample(DateTimeOffset.UtcNow);double diskMs=query.Elapsed.TotalMilliseconds;
                query.Restart();var b=battery.Sample(DateTimeOffset.UtcNow);
                samples.Add(new{at=DateTimeOffset.UtcNow,fans=f,disk=d,battery=b,fan_ms=fanMs,disk_ms=diskMs,battery_ms=query.Elapsed.TotalMilliseconds});
                if(f.Issue is not null || d.Issue is not null || b.Issue is not null)throw new InvalidDataException(JsonSerializer.Serialize(samples,Protocol.Json));
            }
            await Task.Delay(1000);
        }
        fans.Clear();disk.Clear();battery.Clear();process.Refresh();
        var result=new{version=1,at=DateTimeOffset.UtcNow,samples,seconds=wall.Elapsed.TotalSeconds,cpu_ms=process.TotalProcessorTime.TotalMilliseconds-before,
            fan_exchanges=fans.Exchanges,disk_queries=disk.Queries,battery_queries=battery.Queries,all_handles_closed=!fans.Connected&&!disk.Connected&&!battery.Connected,synapse_requests=0};
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);File.WriteAllText(path,JsonSerializer.Serialize(result,Protocol.Json));
        Console.WriteLine(JsonSerializer.Serialize(new{path,result.seconds,result.cpu_ms,result.all_handles_closed,result.fan_exchanges},Protocol.Json));
    }
    public static async Task CollectorLive(string[] args)
    {
        int at=Array.IndexOf(args,"--output");string path=at>=0?args[at+1]:"artifacts/validation/direct-collector-live.json";
        var samples=new List<HardwareReading>();var blocks=new List<object>();var progress=new List<string>{"block,details,seconds,one_core_percent"};
        var sensors=new WindowsSensors(h=>{lock(samples)samples.Add(h);return ValueTask.CompletedTask;},true);
        using var process=Process.GetCurrentProcess();
        bool disposed=false;
        try
        {
            sensors.EnableTaskbarPower(true);sensors.EnableDetails(true);sensors.EnableAuxiliaryDetails(true);sensors.Start();await Task.Delay(6000);
            Check(sensors.DirectReading.Issue is null && sensors.DirectReading.Sensors.Count(s=>s.Value.HasValue)>=18,"Direct collector yields real device readings");
            Check(sensors.AmdReading.Sensors.Any(s=>s.Id=="amd:npu:clock" && s.Value.HasValue),"Live NPU channel available");
            foreach(bool details in new[]{false,true,true,false})
            {
                sensors.EnableDetails(details);sensors.EnableAuxiliaryDetails(details);await Task.Delay(2000);process.Refresh();
                double cpuBefore=process.TotalProcessorTime.TotalMilliseconds;var costs=sensors.CpuMilliseconds;int before;
                lock(samples)before=samples.Count;
                var clock=Stopwatch.StartNew();await Task.Delay(10000);process.Refresh();
                double seconds=clock.Elapsed.TotalSeconds,percent=100*(process.TotalProcessorTime.TotalMilliseconds-cpuBefore)/(seconds*1000);
                var cpu=sensors.CpuMilliseconds.ToDictionary(p=>p.Key,p=>p.Value-costs.GetValueOrDefault(p.Key));
                lock(samples)Check(samples.Count-before>=9,"One-second snapshots remain timely");
                Check(details || !sensors.DirectReading.Sensors.Any(),"Hidden popup has no device readings");
                if(!details)Check(new[]{"blade_fans","nvme_health","battery_details"}.All(key=>cpu.GetValueOrDefault(key)==0),"Hidden popup does no auxiliary sensor polling");
                blocks.Add(new{details,seconds,one_core_percent=percent,cpu_milliseconds=cpu,devices=sensors.DeviceDiagnostics,nvidia=sensors.NvidiaDiagnostics});
                progress.Add($"{blocks.Count},{details},{seconds:R},{percent:R}");
                Console.WriteLine(JsonSerializer.Serialize(blocks[^1],Protocol.Json));
            }
            sensors.Suspend(true);await Task.Delay(2200);
            Check(sensors.Latest.Details.Length==0 && sensors.DirectReading.Sensors.Length==0,"Lock clears readings");
            var paused=sensors.DeviceDiagnostics;
            disposed=true;await sensors.DisposeAsync();
            var result=new{version=1,at=DateTimeOffset.UtcNow,blocks,paused,after_shutdown=sensors.DeviceDiagnostics,nvidia=sensors.NvidiaDiagnostics,
                samples=samples.ToArray(),power_limitation="Process CPU cost, not electrical battery power; AC and disconnected desktop",synapse_requests=0};
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);File.WriteAllText(path,JsonSerializer.Serialize(result,Protocol.Json));
            File.WriteAllLines(Path.ChangeExtension(path,"csv"),progress);
            Console.WriteLine("Direct collector visibility, cadence, pause and shutdown checks passed");
        }
        finally{if(!disposed)await sensors.DisposeAsync();}
    }
    public static async Task CollapsedLive()
    {
        await using var sensors=new WindowsSensors(_=>ValueTask.CompletedTask);
        sensors.EnableTaskbarPower(true);sensors.EnableDetails(true);sensors.Start();await Task.Delay(2500);
        var unopened=JsonSerializer.SerializeToElement(sensors.DeviceDiagnostics,Protocol.Json);
        Check(unopened.GetProperty("fan_exchanges").GetInt64()==0 && unopened.GetProperty("nvme_queries").GetInt64()==0 && unopened.GetProperty("battery_queries").GetInt64()==0
            && unopened.GetProperty("nvidia_extra_queries").GetInt64()==0,"Collapsed sensor section performs no extra device queries");
        sensors.EnableAuxiliaryDetails(true);await Task.Delay(2500);
        Check(sensors.DirectReading.Sensors.Any(s=>s.Id=="blade:fan:1" && s.Value.HasValue),"Expanding sensors starts real fan readings");
        sensors.EnableAuxiliaryDetails(false);await Task.Delay(2200);
        var closed=JsonSerializer.SerializeToElement(sensors.DeviceDiagnostics,Protocol.Json);
        Check(new[]{"fan_connected","nvme_connected","battery_connected","nvidia_extra_initialized"}.All(k=>!closed.GetProperty(k).GetBoolean()) && sensors.DirectReading.Sensors.Length==0,"Collapsing releases extra handles while primary readings continue");
        Check(sensors.AmdReading.PackageWatts.HasValue,"Collapsing extra sensors retains taskbar power");
        Console.WriteLine(JsonSerializer.Serialize(new{unopened,closed,apu_watts=sensors.AmdReading.PackageWatts,samples_continue=sensors.Latest.SampledAt},Protocol.Json));
    }
}
