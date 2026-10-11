param([string]$Package='',
    [string]$Version=([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')),
    [switch]$NoLaunch,[switch]$AcceptThirdPartyTerms,[switch]$ShowThirdPartyTerms,[switch]$VerifyOnly)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
. "$PSScriptRoot/release.ps1"
if(!$Package){$Package=if(Test-Path "$PSScriptRoot/OWY.exe"){$PSScriptRoot}else{Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\package'}}
if($Version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$'){throw 'Invalid version directory name'}
$Package=[IO.Path]::GetFullPath($Package)
Test-OwyRelease $Package
if($VerifyOnly){Write-Output 'Package, notices and corresponding source verified. No installation or agreement recorded.';return}
$terms=@(Get-OwyRuntimeTerms $Package)
if($ShowThirdPartyTerms){foreach($term in $terms){Write-Output $term.file;Write-Output $term.text};return}
if(Get-Process OWY -ErrorAction SilentlyContinue){throw 'Use Stop and quit before installing an update'}
$acceptance=Confirm-OwyRuntimeTerms $terms $AcceptThirdPartyTerms.IsPresent
$data=Join-Path $env:LOCALAPPDATA 'OWY';$target=Join-Path "$data\app" $Version
if(Test-Path $target){throw 'Version already exists; existing builds are preserved'}
New-Item -ItemType Directory -Path $target -Force|Out-Null
Copy-Item "$Package\*" $target -Recurse
[IO.File]::WriteAllText("$target\runtime-terms-acceptance.json",($acceptance|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText("$data\app\current.txt",$Version)
$settingsPath=Join-Path $data 'settings.json'
if(Test-Path $settingsPath){
  $settings=Get-Content $settingsPath -Raw|ConvertFrom-Json
  if($settings.launch_at_sign_in){
    $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try{$key.SetValue('OWY','"'+$target+'\OWY.exe" --background')}finally{$key.Dispose()}
  }
}
$menu=Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs';$shell=New-Object -ComObject WScript.Shell
$shortcut=$shell.CreateShortcut((Join-Path $menu 'OWY.lnk'));$shortcut.TargetPath="$target\OWY.exe";$shortcut.WorkingDirectory=$target
$shortcut.Description="Owl's Watching You - hardware and Codex taskbar monitor";$shortcut.IconLocation="$target\assets\OWY.ico,0";$shortcut.Save()
Write-Output "OWY installed: $target"
if(!$NoLaunch){Start-Process "$target\OWY.exe" -WorkingDirectory $target}
