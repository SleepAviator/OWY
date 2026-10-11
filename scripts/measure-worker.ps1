param([int]$Seconds=20,[string]$Python=(Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\python3139\python.exe'),[string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent),[switch]$Profile)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if ($Seconds -lt 10 -or $Seconds -gt 900) { throw 'Measurement interval must be 10 to 900 seconds' }
$p=New-Object System.Diagnostics.Process
$p.StartInfo.FileName=$Python
$p.StartInfo.Arguments='-B -u '+$(if($Profile){'-m cProfile -o "'+$ProjectRoot+'\artifacts\worker-idle.pstats" '})+'"'+$ProjectRoot+'\worker\owy_worker.py"'
$p.StartInfo.UseShellExecute=$false
$p.StartInfo.CreateNoWindow=$true
$p.StartInfo.RedirectStandardInput=$true
$p.StartInfo.RedirectStandardOutput=$true
$p.StartInfo.RedirectStandardError=$true
try {
    if (!$p.Start()) { throw 'Worker launch failed' }
    $output=$p.StandardOutput.ReadToEndAsync()
    $errors=$p.StandardError.ReadToEndAsync()
    Start-Sleep -Seconds 3
    if ($p.HasExited) { throw ('Worker stopped before measurement: '+$errors.Result) }
    $p.Refresh();$cpu0=$p.TotalProcessorTime.TotalSeconds
    $time=[Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Seconds
    $p.Refresh();$cpu1=$p.TotalProcessorTime.TotalSeconds
    $ram=$p.WorkingSet64;$peak=$p.PeakWorkingSet64;$elapsed=$time.Elapsed.TotalSeconds
    $p.StandardInput.WriteLine('quit');$p.StandardInput.Close()
    if (!$p.WaitForExit(5000)) { throw 'Worker did not exit on quit' }
    if ($p.ExitCode) { throw ('Worker failed: '+$errors.Result) }
    $lines=@($output.Result -split '\r?\n' | Where-Object {$_})
    $last=$lines[-1] | ConvertFrom-Json
    $result=[pscustomobject]@{
        Version=1;Scope='Codex worker only; native UI and helper are not running'
        MeasuredAt=(Get-Date).ToUniversalTime().ToString('o');Samples=$lines.Count;ExitCode=$p.ExitCode
        CpuPercentOneLogicalCore=[math]::Round(100*($cpu1-$cpu0)/$elapsed,3)
        WindowSeconds=[math]::Round($elapsed,2);MemoryMiB=[math]::Round($ram/1MB,1);PeakMemoryMiB=[math]::Round($peak/1MB,1)
        Coverage=$last.coverage;Pending=$last.pending_count;Pace300Tps=$last.pace_300_tps
        TelemetryBatches=$last.telemetry_batches;TelemetryReceivedAt=$last.telemetry_received_at
        MaximumTotalTps=(@($lines|ForEach-Object {($_|ConvertFrom-Json).total_tps})|Measure-Object -Maximum).Maximum
        SystemPowerDeltaWatts=$null;WakeupsMeasured=$false;HelperMeasured=$false;UIValidated=$false
        PortListenersAfterQuit=@(Get-NetTCPConnection -LocalPort 4319 -State Listen -ErrorAction SilentlyContinue).Count
    }
    New-Item -ItemType Directory -Force (Join-Path $ProjectRoot 'artifacts') | Out-Null
    $json=$result | ConvertTo-Json -Depth 3
    $json | Set-Content (Join-Path $ProjectRoot 'artifacts\worker-idle.json') -Encoding UTF8
    $json
}
finally {
    if ($p.Id -and !$p.HasExited) { $p.Kill();$p.WaitForExit() }
    $p.Dispose()
}
