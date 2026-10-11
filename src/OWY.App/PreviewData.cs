using OWY.Core;

namespace OWY.App;

// Deterministic numeric examples only. No device, telemetry, settings or session-log access.
internal static class PreviewData
{
    static long Output(long second)=>8+(long)Math.Round(30*(1+Math.Sin(second/19.0))/2)+(second%47==0?35:0);
    public static CodexReading Codex(DateTimeOffset now)
    {
        long end=now.ToUnixTimeSeconds();
        long tokens=Enumerable.Range(0,300).Sum(i=>Output(end-i));
        return new(1,1,end,48,16,3,tokens/300.0,tokens,false,"complete",[],0,
            [new(20,"Example provider","Example model"),new(16,"Example provider","Example model"),new(12,"Example provider","Example model")],
            [new(end,Output(end),true,false)]);
    }
    public static HardwareReading Hardware(DateTimeOffset now)
    {
        SensorValue Sensor(string id,string name,double value,string unit)=>new(id,name,value,unit,now,"fresh","fictional preview");
        return new(now,5.4,42,14_431_091_712,34_359_738_368,14_000,32_000,12_300,3.2,78,false,false,null,"unavailable",
            [Sensor("cpu_temp","CPU temperature",52,"°C"),Sensor("apu_power","APU package power",11,"W"),
             Sensor("igpu_clock","iGPU clock",800,"MHz"),Sensor("gpu_temp","NVIDIA temperature",45,"°C"),
             Sensor("gpu_power","NVIDIA board power",15,"W"),Sensor("gpu_memory_temp","GPU memory temperature",55,"°C"),
             Sensor("fan_left","Left fan",2400,"RPM"),Sensor("fan_right","Right fan",2200,"RPM"),
             Sensor("nvme_temp","NVMe temperature",38,"°C"),Sensor("battery_voltage","Battery voltage",16.7,"V"),
             Sensor("battery_cycles","Battery cycles",18,"cycles"),Sensor("npu_clock","NPU clock",800,"MHz")],[])
            {ComponentWatts=26,ComponentPowerBasis="APU+dGPU",ActiveGpu="NVIDIA GeForce RTX 5070 Laptop",ActiveGpuKind="discrete",ActiveGpuWatts=15};
    }
    public static void SeedHistory(string directory,DateTimeOffset now)
    {
        using var history=new History(directory,now);
        long end=now.ToUnixTimeSeconds();var hardware=Hardware(now);
        for(long second=end-600;second<end;second++)
        {
            var time=DateTimeOffset.FromUnixTimeSeconds(second);
            var codex=new CodexReading(1,second,second,48,16,3,24,7200,false,"complete",[],0,[],[new(second,Output(second),true,false)]);
            history.Add(new(time,codex,hardware,[]));
        }
    }
}
