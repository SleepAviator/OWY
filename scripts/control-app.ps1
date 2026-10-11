param([ValidateSet('Quit','Launch')][string]$Action,[string]$Version,[int]$QuitAfter=0,[switch]$Show)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($Action -eq 'Launch'){
    if(Get-Process OWY -ErrorAction SilentlyContinue){throw 'OWY is already running'}
    $root=Join-Path $env:LOCALAPPDATA 'OWY\app'
    if(!$Version){$Version=(Get-Content (Join-Path $root 'current.txt') -Raw).Trim()}
    if($Version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$'){throw 'Invalid installed version'}
    $executable=Join-Path $root "$Version\OWY.exe"
    if(!(Test-Path $executable)){throw 'Installed OWY executable is unavailable'}
    $arguments=@('--background');if($Show){$arguments+='--show'}
    if($QuitAfter -gt 0){$arguments+=@('--quit-after',"$QuitAfter")}
    Start-Process $executable -WorkingDirectory (Split-Path $executable) -ArgumentList $arguments
    return
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OwyWindowControl {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string title);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [DllImport("user32.dll",SetLastError=true)] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wp,IntPtr lp);
}
'@
$apps=@(Get-Process OWY -ErrorAction SilentlyContinue)
if($apps.Count -ne 1){throw 'Exactly one OWY instance is required'}
$window=[OwyWindowControl]::FindWindow('OWY.OwnedStrip','OWY')
if($window -eq [IntPtr]::Zero){throw 'Run this command in OWY’s interactive Windows session'}
[uint32]$owner=0;[void][OwyWindowControl]::GetWindowThreadProcessId($window,[ref]$owner)
if($owner -ne $apps[0].Id){throw 'OWY window owner does not match'}
if(![OwyWindowControl]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero)){throw 'OWY quit request failed'}
if(!$apps[0].WaitForExit(15000)){throw 'OWY did not complete its clean shutdown in 15 seconds'}
Write-Output 'OWY stopped cleanly'
