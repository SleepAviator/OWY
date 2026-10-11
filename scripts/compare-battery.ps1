param([int]$Seconds=180,[int]$SettleSeconds=30,[ValidateSet('balanced','quick')][string]$Profile='balanced',[string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($Seconds -lt 30 -or $Seconds -gt 300 -or $SettleSeconds -lt 10 -or $SettleSeconds -gt 60){throw 'Use bounded 30–300 second measurements and 10–60 second settling'}
Set-Location $ProjectRoot
$stamp=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$prefix='battery-ecoqos-'+$stamp
$dir=Join-Path $ProjectRoot 'artifacts\power'
New-Item -ItemType Directory -Force $dir|Out-Null
$summary=Join-Path $dir ($prefix+'-comparison.json')
$progress=Join-Path $dir ($prefix+'-progress.csv')
$state=Join-Path $dir 'battery-comparison-current.json'
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class OwyBatteryContext {
[DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)]static extern bool WTSQuerySessionInformation(IntPtr s,uint id,int c,out IntPtr b,out uint n);
[DllImport("wtsapi32.dll")]static extern void WTSFreeMemory(IntPtr b);
[DllImport("kernel32.dll",SetLastError=true)]public static extern uint SetThreadExecutionState(uint flags);
public static object Session(uint id){IntPtr b;uint n;if(!WTSQuerySessionInformation(IntPtr.Zero,id,25,out b,out n))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{if(n<20||Marshal.ReadInt32(b)!=1)throw new Exception("Session state unavailable");return new{connect_state=Marshal.ReadInt32(b,12),session_flags=Marshal.ReadInt32(b,16),unlocked=Marshal.ReadInt32(b,12)==0&&Marshal.ReadInt32(b,16)==1};}finally{WTSFreeMemory(b);}}
}
'@
function Invoke-OwyControl([string]$Action) {
    $name='OWY-Battery-Control-'+$Action+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $ProjectRoot 'scripts\control-app.ps1')+'" -Action '+$Action
    $actionSpec=New-ScheduledTaskAction -Execute powershell.exe -Argument $arguments
    $principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -LogonType Interactive -RunLevel Limited
    $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
    try {
        Register-ScheduledTask -TaskName $name -Action $actionSpec -Principal $principal -Settings $settings -Force|Out-Null
        Start-ScheduledTask -TaskName $name
        $clock=[Diagnostics.Stopwatch]::StartNew()
        do {Start-Sleep -Milliseconds 500;$apps=@(Get-Process OWY -ErrorAction SilentlyContinue)} while($clock.Elapsed.TotalSeconds -lt 20 -and (($Action -eq 'Quit' -and $apps.Count) -or ($Action -eq 'Launch' -and !$apps.Count)))
        if(($Action -eq 'Quit' -and $apps.Count) -or ($Action -eq 'Launch' -and $apps.Count -ne 1)){throw ('Interactive '+$Action+' failed; task result '+(Get-ScheduledTaskInfo -TaskName $name).LastTaskResult)}
    } finally {Unregister-ScheduledTask -TaskName $name -Confirm:$false}
}
$app=Get-Process OWY
$sessionId=$app.SessionId
$version=[IO.File]::ReadAllText("$env:LOCALAPPDATA\OWY\app\current.txt").Trim()
$settingsHash=(Get-FileHash "$env:LOCALAPPDATA\OWY\settings.json").Hash
$codexHash=(Get-FileHash "$env:USERPROFILE\.codex\config.toml").Hash
$start=[DateTime]::UtcNow.ToString('o');$total=[Diagnostics.Stopwatch]::StartNew()
$phases=[Collections.Generic.List[object]]::new();$failure=$null;$stage='starting';$index=0;$mode='enabled'
$modes=if($Profile -eq 'quick'){@('disabled','enabled')}else{@('enabled','disabled','disabled','enabled')}
$phaseCount=$modes.Count
function Context {
    $battery=@(Get-CimInstance -Namespace root/wmi -Class BatteryStatus|Select-Object PowerOnline,Charging,Discharging,RemainingCapacity,Voltage,DischargeRate)
    if($battery.Count -ne 1 -or $battery[0].PowerOnline -or !$battery[0].Discharging){throw 'Battery comparison requires one discharging battery with AC unplugged'}
    if($battery[0].DischargeRate -le 0 -or $battery[0].DischargeRate -ge 200000){throw 'Valid battery discharge watts are unavailable'}
    $session=[OwyBatteryContext]::Session([uint32]$sessionId)
    if(!$session.unlocked){throw 'Desktop locked or disconnected; stop to avoid comparing different display states'}
    $brightness=@(Get-CimInstance -Namespace root/wmi -Class WmiMonitorBrightness|Where-Object Active|Select-Object CurrentBrightness)
    [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');battery=$battery[0];session=$session;brightness=$brightness;power_plan=(& powercfg /getactivescheme|Out-String).Trim()}
}
function Save-State([string]$Status) {
    $record=[pscustomobject]@{version=1;started_at=$start;prefix=$prefix;status=$Status;phase=$index;mode=$mode;stage=$stage;completed_phases=$phases.Count;total_phases=$phaseCount;
        elapsed_seconds=$total.Elapsed.TotalSeconds;expected_measure_seconds=$Seconds;settle_seconds=$SettleSeconds;report=$summary;progress_csv=$progress;failure=$failure}
    [IO.File]::WriteAllText($state,($record|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{timestamp=[DateTime]::UtcNow.ToString('o');phase=$index;mode=$mode;stage=$stage;status=$Status;completed_phases=$phases.Count;total_phases=$phaseCount;elapsed_seconds=$total.Elapsed.TotalSeconds;remaining_plan_seconds=[Math]::Max(0,($phaseCount-$phases.Count)*($Seconds+$SettleSeconds))}|Export-Csv $progress -NoTypeInformation -Append
}
$initial=Context
$awake=[OwyBatteryContext]::SetThreadExecutionState(2147483651)
if(!$awake){throw 'Cannot keep the display/system awake during matched measurements'}
try {
    Save-State 'running'
    foreach($next in $modes) {
        $index++;$mode=$next;$stage='switching';Save-State 'running'
        $running=@(Get-Process OWY -ErrorAction SilentlyContinue)
        if($mode -eq 'disabled' -and $running.Count){Invoke-OwyControl Quit}
        if($mode -eq 'enabled' -and !$running.Count){Invoke-OwyControl Launch}
        $stage='settling';Save-State 'running';Start-Sleep -Seconds $SettleSeconds
        $before=Context
        if($before.power_plan -ne $initial.power_plan -or ($before.brightness|ConvertTo-Json -Compress) -ne ($initial.brightness|ConvertTo-Json -Compress)){throw 'Power plan or brightness changed'}
        $phase=$prefix+'-'+$index
        $stdout=Join-Path $dir ($phase+'-stdout.json')
        $stage='measuring';Save-State 'running'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ProjectRoot 'scripts\measure-power.ps1') -Mode $mode -Phase $phase -Seconds $Seconds -SampleSeconds 10 -GuardSessionId $sessionId -ProjectRoot $ProjectRoot > $stdout
        if($LASTEXITCODE){throw ('Measurement failed in phase '+$index)}
        $report=@(Get-ChildItem $dir -Filter ('*-'+$phase+'-'+$mode+'.json'))
        if($report.Count -ne 1){throw 'Expected one completed phase report'}
        $data=[IO.File]::ReadAllText($report[0].FullName)|ConvertFrom-Json
        $after=Context
        if($after.power_plan -ne $initial.power_plan -or ($after.brightness|ConvertTo-Json -Compress) -ne ($initial.brightness|ConvertTo-Json -Compress)){throw 'Power plan or brightness changed'}
        if($data.discharge_samples -lt [Math]::Floor($Seconds/15)){throw 'Too few valid battery discharge samples'}
        $phases.Add([pscustomobject]@{index=$index;mode=$mode;report=$report[0].Name;seconds=$data.seconds;mean_discharge_watts=$data.mean_discharge_watts;one_core_percent=$data.one_core_percent;system_cpu_percent=$data.system_cpu_percent;before=$before;after=$after})
        $stage='phase-complete';Save-State 'running'
        [IO.File]::WriteAllText($summary,([pscustomobject]@{version=1;status='running';build=$version;phases=$phases.ToArray()}|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    }
} catch {$failure=$_.Exception.Message;throw}
finally {
    try {if(!(Get-Process OWY -ErrorAction SilentlyContinue)){Invoke-OwyControl Launch}}
    catch {$failure=if($failure){$failure+'; OWY restore failed: '+$_.Exception.Message}else{'OWY restore failed: '+$_.Exception.Message};throw}
    finally {
        if(![OwyBatteryContext]::SetThreadExecutionState(2147483648)){throw 'Cannot restore normal display/system idle behavior'}
        $stage='finished';Save-State $(if($failure){'failed'}else{'complete'})
        $result=[pscustomobject]@{version=1;status=if($failure){'failed'}else{'complete'};started_at=$start;completed_at=[DateTime]::UtcNow.ToString('o');seconds=$total.Elapsed.TotalSeconds;build=$version;
            phase_seconds=$Seconds;settle_seconds=$SettleSeconds;profile=$Profile;order=if($Profile -eq 'quick'){'off/on'}else{'on/off/off/on'};phases=$phases.ToArray();failure=$failure;owy_restored=[bool](Get-Process OWY -ErrorAction SilentlyContinue);
            settings_preserved=(Get-FileHash "$env:LOCALAPPDATA\OWY\settings.json").Hash -eq $settingsHash;codex_config_preserved=(Get-FileHash "$env:USERPROFILE\.codex\config.toml").Hash -eq $codexHash;
            measurement_script_sha256=(Get-FileHash (Join-Path $ProjectRoot 'scripts\measure-power.ps1')).Hash;comparison_script_sha256=(Get-FileHash $PSCommandPath).Hash;
            limitations='Whole-machine battery telemetry with background applications left running. The observer is present in every phase. Instant discharge rates and remaining-capacity deltas need independent comparison; an on/off difference alone does not establish causation or calibrated battery-life savings.'}
        [IO.File]::WriteAllText($summary,($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    }
}
