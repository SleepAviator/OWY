param(
    [ValidateSet('enabled','disabled')][string]$Mode='enabled',
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$Phase='idle',
    [int]$Seconds=120,
    [int]$SampleSeconds=10,
    [int]$GuardSessionId=-1,
    [string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($Seconds -lt 30 -or $Seconds -gt 900 -or $SampleSeconds -lt 5 -or $SampleSeconds -gt 30){throw 'Use 30–900 seconds, sampled every 5–30 seconds'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OwyPowerCounters {
 [StructLayout(LayoutKind.Sequential)] public struct Io {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
 [StructLayout(LayoutKind.Sequential)] struct PropertyKey {public Guid Format;public uint Id;}
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessIoCounters(IntPtr process,out Io value);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
 public static Io ProcessIo(IntPtr process){Io v;if(!GetProcessIoCounters(process,out v))throw new System.ComponentModel.Win32Exception();return v;}
 public static ulong[] Cpu(){ulong i,k,u;if(!GetSystemTimes(out i,out k,out u))throw new System.ComponentModel.Win32Exception();return new ulong[]{i,k+u};}
 [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)] static extern bool WTSQuerySessionInformation(IntPtr s,uint id,int c,out IntPtr b,out uint n);
 [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr b);
 public static bool SessionUnlocked(uint id){
  IntPtr b;uint n;if(!WTSQuerySessionInformation(IntPtr.Zero,id,25,out b,out n))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  try{if(n<20||Marshal.ReadInt32(b)!=1)throw new InvalidOperationException("Session state unavailable");return Marshal.ReadInt32(b,12)==0&&Marshal.ReadInt32(b,16)==1;}
  finally{WTSFreeMemory(b);}
 }
 [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Locate_DevNodeW(out uint node,string id,uint flags);
 [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_PropertyW(uint node,ref PropertyKey key,out uint type,byte[] data,ref uint size,uint flags);
 public static int? DevicePower(string id){
  uint node;if(CM_Locate_DevNodeW(out node,id,0)!=0)return null;
  var key=new PropertyKey{Format=new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),Id=32};
  var data=new byte[56];uint size=(uint)data.Length,type;
  if(CM_Get_DevNode_PropertyW(node,ref key,out type,data,ref size,0)!=0 || type!=0x1003 || size<8)return null;
  int state=BitConverter.ToInt32(data,4);return state>=1 && state<=4?(int?)state:null;
 }
}
'@
$gpuDevices=@(Get-PnpDevice -Class Display|Where-Object {$_.InstanceId -like 'PCI\VEN_10DE*'}|Select-Object FriendlyName,InstanceId)
$ui=@(Get-Process OWY -ErrorAction SilentlyContinue)
if($Mode -eq 'enabled' -and $ui.Count -ne 1){throw 'Exactly one OWY UI must be running'}
if($Mode -eq 'disabled' -and $ui.Count){throw 'Quit OWY before the disabled comparison'}
$worker=@(Get-NetTCPConnection -LocalPort 4319 -State Listen -ErrorAction SilentlyContinue|Select-Object -ExpandProperty OwningProcess -Unique)
if($Mode -eq 'enabled' -and $worker.Count -ne 1){throw 'Exactly one loopback Codex worker must be running'}
if($Mode -eq 'disabled' -and $worker.Count){throw 'A telemetry listener remains running'}
$ownIds=@($ui.Id)+$worker|Where-Object {$_}|Select-Object -Unique
$vendor=@(Get-Process RzDLLService* -ErrorAction SilentlyContinue)
$tracked=@($ownIds)+@($vendor.Id)|Where-Object {$_}|Select-Object -Unique
$start=@{};foreach($id in $tracked){$p=Get-Process -Id $id;$start[$id]=@{cpu=$p.TotalProcessorTime.TotalSeconds;io=[OwyPowerCounters]::ProcessIo($p.Handle)}}
$background=@{};Get-Process|ForEach-Object {if($null -ne $_.CPU){$background[$_.Id]=@{name=$_.ProcessName;cpu=$_.CPU}}}
$planBefore=(& powercfg /getactivescheme | Out-String).Trim()
$stamp=[DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$dir=Join-Path $ProjectRoot 'artifacts\power';New-Item -ItemType Directory -Force $dir|Out-Null
$path=Join-Path $dir "$stamp-$Phase-$Mode.json"
$csv=Join-Path $dir 'benchmark_progress_live_v2.csv'
$samples=[Collections.Generic.List[object]]::new()
$batteryIssue=$null
function Read-Battery {
    try {return @(Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus | Select-Object PowerOnline,Charging,Discharging,RemainingCapacity,Voltage,ChargeRate,DischargeRate)}
    catch [Microsoft.Management.Infrastructure.CimException] {$script:batteryIssue='Battery status unavailable: '+$_.Exception.Message;return @()}
}
$batteryBefore=Read-Battery
if($GuardSessionId -ge 0 -and ![OwyPowerCounters]::SessionUnlocked([uint32]$GuardSessionId)){throw 'Desktop locked or disconnected before measurement'}
$sysBefore=[OwyPowerCounters]::Cpu();$clock=[Diagnostics.Stopwatch]::StartNew()
while($clock.Elapsed.TotalSeconds -lt $Seconds){
    Start-Sleep -Milliseconds ([int](1000*[Math]::Min($SampleSeconds,$Seconds-$clock.Elapsed.TotalSeconds)))
    $elapsed=$clock.Elapsed.TotalSeconds
    if($GuardSessionId -ge 0 -and ![OwyPowerCounters]::SessionUnlocked([uint32]$GuardSessionId)){throw 'Desktop locked or disconnected during measurement'}
    $processes=@(foreach($id in $tracked){
        $p=Get-Process -Id $id
        $io=[OwyPowerCounters]::ProcessIo($p.Handle)
        [pscustomobject]@{name=$p.ProcessName;pid=$id;scope=if($ownIds -contains $id){'OWY'}else{'Razer'};
            cpu_seconds=$p.TotalProcessorTime.TotalSeconds-$start[$id].cpu;
            one_core_percent=100*($p.TotalProcessorTime.TotalSeconds-$start[$id].cpu)/$elapsed;
            working_set_bytes=$p.WorkingSet64;private_bytes=$p.PrivateMemorySize64;threads=$p.Threads.Count;
            io_read_bytes=$io.ReadBytes-$start[$id].io.ReadBytes;io_write_bytes=$io.WriteBytes-$start[$id].io.WriteBytes;
            io_other_operations=$io.OtherOperations-$start[$id].io.OtherOperations}
    })
    $system=[OwyPowerCounters]::Cpu();$total=[double]($system[1]-$sysBefore[1]);$idle=[double]($system[0]-$sysBefore[0])
    $row=[pscustomobject]@{at=[DateTimeOffset]::UtcNow.ToString('o');elapsed_seconds=$elapsed;processes=$processes;
        system_cpu_percent=if($total -gt 0){100*(1-$idle/$total)}else{$null};battery=(Read-Battery);
        nvidia_device_power=@($gpuDevices|ForEach-Object {[pscustomobject]@{name=$_.FriendlyName;state=[OwyPowerCounters]::DevicePower($_.InstanceId)}})}
    $samples.Add($row)
    [pscustomobject]@{timestamp=$row.at;phase=$Phase;mode=$Mode;status='running';
        measured_seconds=[Math]::Round($elapsed,2);remaining_seconds=[Math]::Max(0,[Math]::Round($Seconds-$elapsed,2));
        one_core_percent=(@($processes|Where-Object scope -eq 'OWY')|Measure-Object one_core_percent -Sum).Sum;
        system_cpu_percent=$row.system_cpu_percent}|Export-Csv $csv -NoTypeInformation -Append
}
$last=$samples[$samples.Count-1]
[pscustomobject]@{timestamp=[DateTimeOffset]::UtcNow.ToString('o');phase=$Phase;mode=$Mode;status='complete';
    measured_seconds=[Math]::Round($last.elapsed_seconds,2);remaining_seconds=0;
    one_core_percent=if($ownIds.Count){(@($last.processes|Where-Object scope -eq 'OWY')|Measure-Object one_core_percent -Sum).Sum}else{0};
    system_cpu_percent=$last.system_cpu_percent}|Export-Csv $csv -NoTypeInformation -Append
$backgroundDelta=@(Get-Process|ForEach-Object {
    if($background.ContainsKey($_.Id) -and $null -ne $_.CPU){[pscustomobject]@{name=$_.ProcessName;pid=$_.Id;one_core_percent=100*($_.CPU-$background[$_.Id].cpu)/$last.elapsed_seconds}}
}|Sort-Object one_core_percent -Descending|Select-Object -First 10)
$rates=@($samples|ForEach-Object {$_.battery}|Where-Object {$_.Discharging -and !$_.PowerOnline -and $_.DischargeRate -gt 0 -and $_.DischargeRate -lt 200000}|ForEach-Object {$_.DischargeRate/1000.0})
$own=@($last.processes|Where-Object scope -eq 'OWY')
$result=[pscustomobject]@{version=2;phase=$Phase;mode=$Mode;measured_at=$last.at;seconds=$last.elapsed_seconds;logical_processors=[Environment]::ProcessorCount;guard_session_id=$GuardSessionId;
    one_core_percent=if($own.Count){($own|Measure-Object one_core_percent -Sum).Sum}else{0};working_set_bytes=if($own.Count){($own|Measure-Object working_set_bytes -Sum).Sum}else{0};
    system_cpu_percent=$last.system_cpu_percent;processes=$last.processes;top_background_cpu=$backgroundDelta;
    mean_discharge_watts=if($rates.Count){($rates|Measure-Object -Average).Average}else{$null};discharge_samples=$rates.Count;
    battery_before=$batteryBefore;battery_issue=$batteryIssue;power_plan_before=$planBefore;power_plan_after=(& powercfg /getactivescheme|Out-String).Trim();
    samples=$samples.ToArray();limitations='Process I/O includes IPC and is not physical SSD writes. CPU is percent of one logical core. Battery discharge is whole-machine telemetry; attribution needs matched repeated runs. GPU power states are passive Windows most-recent states (1=D0, 4=D3), not electrical measurements or GPU utilization. Wakeups are not measured here.'}
[IO.File]::WriteAllText($path,($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$result|Select-Object version,phase,mode,seconds,one_core_percent,working_set_bytes,system_cpu_percent,processes,mean_discharge_watts,discharge_samples,@{n='report';e={$path}}|ConvertTo-Json -Depth 5
