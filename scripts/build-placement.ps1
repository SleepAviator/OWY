param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'
Set-Location $ProjectRoot
$dotnet=Join-Path $ProjectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path $dotnet)) { throw 'Run scripts/bootstrap.ps1 first' }
& $dotnet publish src/OWY.Placement -c Release -r win-x64 --self-contained true -o artifacts/placement -v minimal
if ($LASTEXITCODE) { throw 'Placement build failed' }
& $dotnet build tests/OWY.Core.Tests -c Release -v minimal
if ($LASTEXITCODE) { throw 'Core regression build failed' }
$files=Get-ChildItem artifacts/placement -File | Where-Object {$_.Name -in 'OWY.Placement.exe','OWY.Placement.dll','OWY.Taskbar.dll'}
$manifest=@($files | ForEach-Object {[pscustomobject]@{File=$_.Name;SHA256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();Signature=(Get-AuthenticodeSignature $_.FullName).Status.ToString()}})
$manifest | ConvertTo-Json -Depth 4 | Set-Content artifacts/placement/signing-manifest.json -Encoding UTF8
Write-Output 'Placement package prepared. Signing is required when Smart App Control is enforced.'
