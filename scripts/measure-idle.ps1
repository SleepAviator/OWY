param([ValidateSet('enabled','disabled')][string]$Mode='enabled',[int]$Seconds=60,[string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($Seconds -lt 20 -or $Seconds -gt 600){throw 'Use a bounded 20 to 600 second sample'}
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class OWYPowerSample {
 [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
 public static ulong[] Cpu(){ulong i,k,u;if(!GetSystemTimes(out i,out k,out u))throw new System.ComponentModel.Win32Exception();return new ulong[]{i,k+u};}
}
'@
$ui=@(Get-Process OWY -ErrorAction SilentlyContinue)
if($Mode -eq 'enabled' -and $ui.Count -ne 1){throw 'Exactly one running OWY UI is required'}
if($Mode -eq 'disabled' -and $ui.Count){throw 'Quit OWY before the disabled comparison'}
$worker=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|Where-Object {$_.LocalPort -eq 4319 -and $_.LocalAddress -eq '127.0.0.1'}|Select-Object -ExpandProperty OwningProcess -Unique)
$ids=@($ui|Select-Object -ExpandProperty Id)+$worker
$before=@{};foreach($id in $ids){$p=Get-Process -Id $id;$before[$id]=$p.TotalProcessorTime.TotalSeconds}
$sysBefore=[OWYPowerSample]::Cpu();$clock=[System.Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $Seconds
$elapsed=$clock.Elapsed.TotalSeconds;$sysAfter=[OWYPowerSample]::Cpu()
$processes=@(foreach($id in $ids){$p=Get-Process -Id $id;[pscustomobject]@{name=$p.ProcessName;pid=$id;one_core_percent=100*($p.TotalProcessorTime.TotalSeconds-$before[$id])/$elapsed;working_set_bytes=$p.WorkingSet64;peak_working_set_bytes=$p.PeakWorkingSet64;threads=$p.Threads.Count}})
$gpu=@();$gpuIssue=$null
try{
 $counters=Get-Counter '\GPU Process Memory(*)\Dedicated Usage','\GPU Process Memory(*)\Shared Usage' -ErrorAction Stop
 $gpu=@($counters.CounterSamples|Where-Object {$sample=$_;$ids|Where-Object {$sample.InstanceName -match ('^pid_'+$_+'_')}}|ForEach-Object{[pscustomobject]@{instance=$_.InstanceName;counter=$_.Path.Split('\')[-1];bytes=$_.CookedValue}})
}catch{$gpuIssue='GPU process counters unavailable: '+$_.Exception.Message}
$systemTotal=[double]($sysAfter[1]-$sysBefore[1]);$systemIdle=[double]($sysAfter[0]-$sysBefore[0])
$result=[pscustomobject]@{version=1;measured_at=[DateTimeOffset]::UtcNow.ToString('o');mode=$Mode;seconds=$elapsed;
 one_core_percent=if($processes.Count){($processes|Measure-Object one_core_percent -Sum).Sum}else{0};logical_processors=[Environment]::ProcessorCount;
 system_cpu_percent=if($systemTotal -gt 0){100*(1-$systemIdle/$systemTotal)}else{$null};processes=$processes;gpu_process_memory=$gpu;gpu_issue=$gpuIssue;
 system_watts=$null;power_limitation='AC without a valid whole-system sensor; CPU and GPU counters are resource proxies, not watts. Wakeups are not measured.'}
$dir=Join-Path $ProjectRoot 'artifacts\power';New-Item -ItemType Directory -Force $dir|Out-Null
[System.IO.File]::WriteAllText((Join-Path $dir ("idle-$Mode.json")),($result|ConvertTo-Json -Depth 5),(New-Object System.Text.UTF8Encoding($false)))
$row=[pscustomobject]@{timestamp=$result.measured_at;phase=$Mode;status='complete';measured_seconds=$elapsed;one_core_percent=$result.one_core_percent;system_cpu_percent=$result.system_cpu_percent}
$row|Export-Csv (Join-Path $dir 'benchmark_progress_live.csv') -NoTypeInformation -Append
$result|ConvertTo-Json -Depth 5
