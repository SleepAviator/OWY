using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using OWY.Core;

namespace OWY.Hardware;

public sealed class WindowsSensors : IAsyncDisposable
{
    readonly Func<HardwareReading,ValueTask> publish;
    readonly CancellationTokenSource stop=new();
    readonly bool profile;readonly Dictionary<string,double> costs=[];
    public IReadOnlyDictionary<string,double> CpuMilliseconds{get{lock(costs)return new Dictionary<string,double>(costs);}}
    Task? run;long ecoQoSCycles;
    public long EcoQoSCycles=>Interlocked.Read(ref ecoQoSCycles);
    volatile bool suspended;
    volatile bool networkDirty=true;
    readonly Dictionary<string,(long Sent,long Received)> counters=[];
    HardwareReading latest=HardwareReading.Empty;
    volatile bool taskbarPowerEnabled,nativeDetailsEnabled,auxiliaryDetailsEnabled;
    GpuSelection selection=GpuSelection.Unknown;long gpuObserved;
    AmdReading amdReading=AmdReading.Empty;
    NvidiaReading nvidiaReading=NvidiaReading.Empty;
    public NvidiaReading NvidiaReading=>Volatile.Read(ref nvidiaReading);
    public object? NvidiaDiagnostics {get;private set;}
    IReadOnlyDictionary<string,double>? adapterLoads;
    public IReadOnlyDictionary<string,double>? GpuAdapterLoads=>Volatile.Read(ref adapterLoads);
    public HardwareReading Latest=>latest;
    public DeviceReading DirectReading {get;private set;}=DeviceReading.Empty;
    public object? DeviceDiagnostics {get;private set;}
    public GpuSelection GpuSelection=>Volatile.Read(ref selection);
    public AmdReading AmdReading=>Volatile.Read(ref amdReading);
    public WindowsSensors(Func<HardwareReading,ValueTask> publish,bool profile=false)
    {this.publish=publish;this.profile=profile;}
    bool CanPollNvidia()
    {
        long observed=Interlocked.Read(ref gpuObserved);
        return !suspended && GpuSelection.AllowNvidiaSensors && observed!=0 && Stopwatch.GetElapsedTime(observed)<TimeSpan.FromSeconds(2.5);
    }
    double CpuClock()
    {
        if(!profile)return 0;
        if(!Native.GetThreadTimes(Native.GetCurrentThread(),out _,out _,out ulong kernel,out ulong user))throw new System.ComponentModel.Win32Exception();
        return (kernel+user)/10000.0;
    }
    void Cost(string name,ref double before){if(!profile)return;double after=CpuClock();lock(costs)costs[name]=costs.GetValueOrDefault(name)+after-before;before=after;}
    public void Start(){NetworkChange.NetworkAddressChanged+=NetworkChanged;NetworkChange.NetworkAvailabilityChanged+=NetworkChanged;run=Task.Run(Run);}
    void NetworkChanged(object? sender,EventArgs args)=>networkDirty=true;
    public void Suspend(bool value)=>suspended=value;
    public void EnableDetails(bool value)=>nativeDetailsEnabled=value;
    public void EnableAuxiliaryDetails(bool value)=>auxiliaryDetailsEnabled=value;
    bool CanReadAuxiliary()=>!suspended && nativeDetailsEnabled && auxiliaryDetailsEnabled;
    public void EnableTaskbarPower(bool value)=>taskbarPowerEnabled=value;
    async Task Run()
    {
        using var disk=new PdhCounter(@"\PhysicalDisk(_Total)\Disk Bytes/sec");
        using var gpu=new PdhCounter(@"\GPU Engine(*)\Utilization Percentage");
        using var amd=new AmdSensors();
        using var nvidia=new NvidiaSensors(CanPollNvidia);
        using var nvidiaExtras=new NvidiaExtraSensors(()=>CanReadAuxiliary() && CanPollNvidia());
        using var fans=new BladeFanSensors();
        using var nvme=new NvmeSensors();
        using var batteryDetails=new BatterySensors();
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        ulong oldIdle=0,oldTotal=0;
        double? cpu=null,memory=null,used=null,total=null,up=null,down=null,diskRate=null,gpuRate=null,watts=null,battery=null;
        long tick=0;double lastNetwork=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        var capacity=new List<SensorValue>();bool wasSuspended=false,hadDetails=false;
        long nextFans=0,nextNvme=0,nextBattery=0;
        NetworkInterface[] interfaces=[];
        GpuAdapter[] adapters=[];HashSet<string> displays=[];
        try
        {
            while(await timer.WaitForNextTickAsync(stop.Token))
            {
                using(EcoQoS.EnterThread())
                {
                    var now=DateTimeOffset.UtcNow;tick++;
                    double cost=CpuClock();
                    var issues=new List<string>();
                    bool powerValid=Native.GetSystemPowerStatus(out var power);
                    bool saver=powerValid && power.Saver==1,onBattery=powerValid && power.Ac==0;
                    if(!powerValid)issues.Add("Battery status unavailable");
                    if(suspended)
                    {
                        Interlocked.Exchange(ref gpuObserved,0);Volatile.Write(ref selection,GpuSelection.Unknown);
                        amd.Clear();Volatile.Write(ref amdReading,amd.Latest);
                        nvidia.Clear();Volatile.Write(ref nvidiaReading,nvidia.Latest);
                        fans.Clear();nvme.Clear();batteryDetails.Clear();nvidiaExtras.Clear();DirectReading=DeviceReading.Empty;hadDetails=false;
                        wasSuspended=true;oldTotal=0;counters.Clear();
                        latest=HardwareReading.Empty with{SampledAt=now,Issues=["Hardware sampling paused while locked or asleep"]};
                    }
                    else
                    {
                        if(wasSuspended){oldTotal=0;counters.Clear();networkDirty=true;wasSuspended=false;tick=1;}
                        if(tick==1 || tick%(saver?4:2)==0)
                        {
                            if(Native.GetSystemTimes(out var idle,out var kernel,out var user))
                            {
                                ulong current=kernel+user;
                                cpu=oldTotal>0 && current>oldTotal?Math.Clamp(100.0*(1-(idle-oldIdle)/(double)(current-oldTotal)),0,100):null;
                                oldIdle=idle;oldTotal=current;
                            }
                            else{cpu=null;issues.Add("CPU counters unavailable");}
                            var mem=new Native.MemoryStatus{Length=(uint)Marshal.SizeOf<Native.MemoryStatus>()};
                            if(Native.GlobalMemoryStatusEx(ref mem)){total=mem.TotalPhys;used=mem.TotalPhys-mem.AvailPhys;memory=100*used/total;}
                            else{memory=used=total=null;issues.Add("Memory status unavailable");}
                        }
                        Cost("cpu_memory",ref cost);
                        double clock=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency,elapsed=clock-lastNetwork;lastNetwork=clock;
                        try
                        {
                            if(networkDirty || tick%30==0)
                            {
                                networkDirty=false;
                                interfaces=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211).ToArray();
                            }
                            double sent=0,received=0;bool valid=false;var active=new HashSet<string>();
                            foreach(var nic in interfaces)
                            {
                                active.Add(nic.Id);var n=nic.GetIPStatistics();
                                if(counters.TryGetValue(nic.Id,out var before) && n.BytesSent>=before.Sent && n.BytesReceived>=before.Received)
                                {sent+=n.BytesSent-before.Sent;received+=n.BytesReceived-before.Received;valid=true;}
                                counters[nic.Id]=(n.BytesSent,n.BytesReceived);
                            }
                            foreach(var id in counters.Keys.Where(x=>!active.Contains(x)).ToArray())counters.Remove(id);
                            up=valid && elapsed is >0 and <4?sent/elapsed:null;down=valid && elapsed is >0 and <4?received/elapsed:null;
                        }
                        catch(NetworkInformationException ex){up=down=null;issues.Add("Network counters unavailable: "+ex.ErrorCode);}
                        Cost("network",ref cost);
                        bool needComponents=nativeDetailsEnabled || (taskbarPowerEnabled && !(onBattery && watts.HasValue));
                        // GPU Engine counters are maintained by Windows. Reading them
                        // does not initialize NVIDIA's hardware-monitoring APIs.
                        if(needComponents || tick==1 || tick%(saver?10:5)==0)
                        {
                            if(tick==1 || tick%60==0)
                            {
                                try{adapters=GpuTopology.Adapters();}
                                catch(InvalidDataException ex){adapters=[];issues.Add(ex.Message);}
                            }
                            if(tick==1 || tick%(saver?10:5)==0)displays=GpuTopology.ActiveDisplays();
                            gpuRate=gpu.ReadGpu();
                            Volatile.Write(ref adapterLoads,gpu.AdapterLoads);
                            Volatile.Write(ref selection,OWY.Hardware.GpuSelection.Select(adapters,gpu.AdapterLoads,displays));
                            Interlocked.Exchange(ref gpuObserved,Stopwatch.GetTimestamp());
                            Cost("gpu",ref cost);
                        }
                        if(tick==1 || tick%(saver?10:5)==0)
                        {
                            diskRate=disk.Read();Cost("disk",ref cost);
                            if(disk.Issue is {} d)issues.Add(d);if(gpu.Issue is {} g)issues.Add(g);
                            battery=powerValid && power.Percent<=100?power.Percent:null;
                            watts=null;
                            if(Native.CallNtPowerInformation(5,0,0,out var state,(uint)Marshal.SizeOf<Native.BatteryState>())==0)
                                watts=DischargeWatts(state.Ac!=0,state.Charging!=0,state.Discharging!=0,state.Rate);
                        }
                        Cost("battery",ref cost);
                        if(tick==1 || tick%60==0)
                        {
                            capacity.Clear();
                            foreach(var drive in DriveInfo.GetDrives().Where(x=>x.DriveType==DriveType.Fixed && x.IsReady))
                            {
                                try{capacity.Add(new("storage:"+drive.Name,drive.Name+" used",drive.TotalSize-drive.AvailableFreeSpace,"bytes",now,"fresh","Windows"));}
                                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){issues.Add("Storage capacity unavailable: "+ex.GetType().Name);}
                            }
                        }
                        Cost("capacity",ref cost);
                        if(CanReadAuxiliary())
                        {
                            if(!hadDetails){nextFans=nextNvme=nextBattery=0;hadDetails=true;}
                            long clockMs=Environment.TickCount64;
                            if(clockMs>=nextFans){fans.Sample(CanReadAuxiliary);nextFans=clockMs+(saver?10000:5000);}
                            Cost("blade_fans",ref cost);
                            if(CanReadAuxiliary() && clockMs>=nextNvme){nvme.Sample(now);nextNvme=clockMs+(saver?300000:60000);}
                            Cost("nvme_health",ref cost);
                            if(CanReadAuxiliary() && clockMs>=nextBattery){batteryDetails.Sample(now);nextBattery=clockMs+(saver?10000:5000);}
                            Cost("battery_details",ref cost);
                            if(CanReadAuxiliary())
                            {
                                var parts=new[]{fans.Latest,nvme.Latest,batteryDetails.Latest};
                                var gaps=parts.Where(p=>p.Issue is not null).Select(p=>p.Issue!).ToArray();
                                DirectReading=new(parts.SelectMany(p=>p.Sensors).ToArray(),gaps.Length>0?string.Join(" · ",gaps):null);
                            }
                            else{fans.Clear();nvme.Clear();batteryDetails.Clear();DirectReading=DeviceReading.Empty;hadDetails=false;}
                        }
                        else if(hadDetails){fans.Clear();nvme.Clear();batteryDetails.Clear();DirectReading=DeviceReading.Empty;hadDetails=false;}
                        double? systemWatts=onBattery?watts:null;
                        needComponents=nativeDetailsEnabled || (taskbarPowerEnabled && !systemWatts.HasValue);
                        if(needComponents)
                        {
                            if(tick==1 || tick%(saver?10:5)==0 || amd.Latest==AmdReading.Empty)
                                Volatile.Write(ref amdReading,amd.Sample(now));
                        }
                        else{amd.Clear();Volatile.Write(ref amdReading,amd.Latest);}
                        Cost("amd",ref cost);
                        if(needComponents && CanPollNvidia())
                        {
                            if(tick==1 || tick%(saver?10:5)==0 || nvidia.Latest==NvidiaReading.Empty)
                                nvidia.Sample(GpuSelection,now);
                            Volatile.Write(ref nvidiaReading,nvidia.Latest.Expire(now,TimeSpan.FromSeconds(saver?30:15)));
                        }
                        else{nvidia.Clear();Volatile.Write(ref nvidiaReading,nvidia.Latest);}
                        Cost("nvidia",ref cost);
                        if(CanReadAuxiliary() && CanPollNvidia())
                        {
                            if(tick==1 || tick%(saver?10:5)==0 || nvidiaExtras.Latest==DeviceReading.Empty)nvidiaExtras.Sample(GpuSelection,now);
                        }
                        else nvidiaExtras.Clear();
                        Cost("nvidia_extras",ref cost);
                        var details=capacity.ToList();
                        var a=AmdReading;var nv=NvidiaReading;var selected=GpuSelection;
                        details.AddRange(a.Sensors);details.AddRange(nv.Sensors);details.AddRange(nvidiaExtras.Latest.Sensors);
                        details.AddRange(DirectReading.Sensors);
                        if(DirectReading.Issue is {} di)issues.Add(di);
                        if(a.Issue is {} ai && needComponents)issues.Add(ai);
                        if(nv.Issue is {} ni && needComponents)issues.Add(ni);
                        if(nvidiaExtras.Latest.Issue is {} ne && CanReadAuxiliary())issues.Add(ne);
                        if(!details.Any(s=>s.Id=="amd:cpu:temperature"))details.Add(new("cpu:temperature","CPU temperature",null,"°C",now,"unavailable","AMD driver"));
                        details.Add(new("gpu:load","Busiest GPU engine",gpuRate,"%",now,gpuRate.HasValue?"fresh":"unavailable","Windows performance counters"));
                        var component=OWY.Hardware.GpuSelection.Power(selected,a,nv);
                        double? selectedPower=selected.SelectedPower(a,nv);
                        latest=new(now,cpu,memory,(long?)used,(long?)total,up,down,diskRate,gpuRate,battery,onBattery,saver,
                            systemWatts,systemWatts.HasValue?"Battery discharge":"unavailable",details.ToArray(),issues.ToArray())
                            {ComponentWatts=component.Watts,ComponentPowerBasis=component.Basis,ActiveGpu=selected.Name,
                             ActiveGpuKind=selected.Kind,ActiveGpuWatts=selectedPower,GpuSelectionBasis=selected.Basis};
                        Cost("snapshot",ref cost);
                    }
                    NvidiaDiagnostics=new{nvidia.Initialized,nvidia.Connections,nvidia.Samples,nvidia.Releases};
                    DeviceDiagnostics=new{fan_exchanges=fans.Exchanges,nvme_queries=nvme.Queries,battery_queries=batteryDetails.Queries,
                        fan_connected=fans.Connected,nvme_connected=nvme.Connected,battery_connected=batteryDetails.Connected,
                        nvidia_extra_initialized=nvidiaExtras.Initialized,nvidia_extra_connections=nvidiaExtras.Connections,nvidia_extra_queries=nvidiaExtras.Queries,nvidia_extra_releases=nvidiaExtras.Releases};
                    Interlocked.Increment(ref ecoQoSCycles);
                }
                await publish(latest);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidDataException)
        {
            string code=ex is System.ComponentModel.Win32Exception windows?" (Windows error "+windows.NativeErrorCode+")":"";
            latest=HardwareReading.Empty with{SampledAt=DateTimeOffset.UtcNow,Issues=["Hardware collector failed: "+ex.Message+code]};
            await publish(latest);throw;
        }
        finally
        {
            try{nvidia.Dispose();}
            finally
            {
                fans.Clear();nvme.Clear();batteryDetails.Clear();nvidiaExtras.Clear();
                NvidiaDiagnostics=new{nvidia.Initialized,nvidia.Connections,nvidia.Samples,nvidia.Releases};
                DeviceDiagnostics=new{fan_exchanges=fans.Exchanges,nvme_queries=nvme.Queries,battery_queries=batteryDetails.Queries,
                    fan_connected=fans.Connected,nvme_connected=nvme.Connected,battery_connected=batteryDetails.Connected,
                    nvidia_extra_initialized=nvidiaExtras.Initialized,nvidia_extra_connections=nvidiaExtras.Connections,nvidia_extra_queries=nvidiaExtras.Queries,nvidia_extra_releases=nvidiaExtras.Releases};
            }
        }
    }
    // SYSTEM_BATTERY_STATE.Rate is a signed LONG in mW, despite the DWORD declaration.
    public static double? DischargeWatts(bool ac,bool charging,bool discharging,int rate)=>
        !ac && !charging && discharging && rate<0 && rate!=int.MinValue && rate!=-1 && -rate<=2_000_000?-rate/1000.0:null;
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try{await (run??Task.CompletedTask);}
        finally{NetworkChange.NetworkAddressChanged-=NetworkChanged;NetworkChange.NetworkAvailabilityChanged-=NetworkChanged;stop.Dispose();}
    }
}

sealed class PdhCounter : IDisposable
{
    nint query,counter;
    public string? Issue {get;private set;}
    public IReadOnlyDictionary<string,double>? AdapterLoads {get;private set;}
    public PdhCounter(string path)
    {
        uint status=Native.PdhOpenQuery(null,0,out query);
        if(status==0)status=Native.PdhAddEnglishCounter(query,path,0,out counter);
        if(status!=0){Issue=(path.Contains("GPU")?"GPU":"Disk")+" counter unavailable (0x"+status.ToString("x")+")";counter=0;}
        if(counter!=0)Native.PdhCollectQueryData(query);
    }
    public double? Read()
    {
        if(counter==0)return null;
        if(Native.PdhCollectQueryData(query)!=0)return null;
        var status=Native.PdhGetFormattedCounterValue(counter,0x200,out _,out var value);
        return status==0 && value.Status<=1 && double.IsFinite(value.Value)&&value.Value>=0?value.Value:null;
    }
    public double? ReadGpu()
    {
        AdapterLoads=null;
        if(counter==0 || Native.PdhCollectQueryData(query)!=0)return null;
        uint size=0,count=0;uint status=Native.PdhGetFormattedCounterArray(counter,0x200,ref size,ref count,0);
        if(status!=0x800007d2 || size>4*1024*1024)return null;
        var buffer=Marshal.AllocHGlobal((int)size);
        try
        {
            if(Native.PdhGetFormattedCounterArray(counter,0x200,ref size,ref count,buffer)!=0)return null;
            var engines=new Dictionary<string,double>();int itemSize=Marshal.SizeOf<Native.CounterItem>();
            if(count*itemSize>size)return null;
            for(int i=0;i<count;i++)
            {
                var item=Marshal.PtrToStructure<Native.CounterItem>(buffer+i*itemSize);string name=Marshal.PtrToStringUni(item.Name)??"";
                int at=name.IndexOf("luid_",StringComparison.Ordinal);
                if(at<0 || item.Data.Status>1 || !double.IsFinite(item.Data.Value) || item.Data.Value<0)continue;
                string key=name[at..].ToLowerInvariant();engines[key]=engines.GetValueOrDefault(key)+item.Data.Value;
            }
            var adapters=new Dictionary<string,double>();
            foreach(var engine in engines)
            {
                int end=engine.Key.IndexOf("_phys_",StringComparison.Ordinal);if(end<0)continue;
                string key=engine.Key[..end];adapters[key]=Math.Max(adapters.GetValueOrDefault(key),Math.Clamp(engine.Value,0,100));
            }
            AdapterLoads=adapters;
            return engines.Count>0?Math.Clamp(engines.Values.Max(),0,100):null;
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    public void Dispose(){if(query!=0)Native.PdhCloseQuery(query);}
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]internal struct MemoryStatus{public uint Length,Load;public ulong TotalPhys,AvailPhys,TotalPage,AvailPage,TotalVirtual,AvailVirtual,AvailExtended;}
    [StructLayout(LayoutKind.Sequential)]internal struct PowerStatus{public byte Ac,Flag,Percent,Saver;public uint Life,FullLife;}
    [StructLayout(LayoutKind.Sequential)]internal struct BatteryState{public byte Ac,Present,Charging,Discharging,Spare1,Spare2,Spare3,Tag;public uint Max,Remaining;public int Rate;public uint Time,Alert1,Alert2;}
    [StructLayout(LayoutKind.Sequential)]internal struct CounterValue{public uint Status;public double Value;}
    [StructLayout(LayoutKind.Sequential)]internal struct CounterItem{public nint Name;public CounterValue Data;}
    [DllImport("kernel32.dll")]internal static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll")]internal static extern nint GetCurrentThread();
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetThreadTimes(nint thread,out ulong created,out ulong closed,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll")]internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus value);
    [DllImport("kernel32.dll")]internal static extern bool GetSystemPowerStatus(out PowerStatus value);
    [DllImport("powrprof.dll")]internal static extern uint CallNtPowerInformation(uint level,nint input,uint inSize,out BatteryState output,uint outSize);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,EntryPoint="PdhOpenQueryW")]internal static extern uint PdhOpenQuery(string? source,nuint user,out nint query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,EntryPoint="PdhAddEnglishCounterW")]internal static extern uint PdhAddEnglishCounter(nint query,string path,nuint user,out nint counter);
    [DllImport("pdh.dll")]internal static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")]internal static extern uint PdhGetFormattedCounterValue(nint counter,uint format,out uint type,out CounterValue value);
    [DllImport("pdh.dll",EntryPoint="PdhGetFormattedCounterArrayW")]internal static extern uint PdhGetFormattedCounterArray(nint counter,uint format,ref uint size,ref uint count,nint items);
    [DllImport("pdh.dll")]internal static extern uint PdhCloseQuery(nint query);
}
