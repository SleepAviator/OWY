param([string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent),[string]$OutputDirectory='')
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\','/')
. "$ProjectRoot/scripts/release.ps1"
Assert-OwyDependencyLicenses $ProjectRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1'
Set-Location $ProjectRoot
$dotnet=Join-Path $ProjectRoot '.tools\dotnet\dotnet.exe'
if(!(Test-Path $dotnet)){throw 'Run scripts/bootstrap.ps1 first'}
$python=Join-Path $ProjectRoot '.tools\python3139'
if(!(Test-Path "$python\python.exe")){
  $archive=Join-Path $ProjectRoot '.tools\python-3.13.9-embed-amd64.zip'
  Invoke-WebRequest 'https://www.python.org/ftp/python/3.13.9/python-3.13.9-embed-amd64.zip' -UseBasicParsing -OutFile $archive
  # SHA-256 published in python.org's Sigstore bundle for this exact release.
  $expected=[BitConverter]::ToString([Convert]::FromBase64String('kdgowto6AptBaZ6RhnSgyzecAs8g2rnFATBohfg3QCo=')).Replace('-','').ToLowerInvariant()
  if((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'Official Python runtime checksum mismatch'}
  Expand-Archive $archive -DestinationPath $python;Remove-Item $archive
  [System.IO.File]::WriteAllText("$python\python313._pth","python313.zip`r`n.`r`n..\worker`r`n",[System.Text.Encoding]::ASCII)
}
& $dotnet build tests/OWY.Core.Tests/OWY.Core.Tests.csproj -c Release -r win-x64 -p:RestoreLockedMode=true -v minimal
if($LASTEXITCODE){throw 'Core test build failed'}
& $dotnet 'tests\OWY.Core.Tests\bin\Release\net10.0-windows10.0.26100.0\win-x64\OWY.Core.Tests.dll'
if($LASTEXITCODE){throw 'Core regressions failed'}
$package=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $ProjectRoot 'artifacts\package'}
if((Test-Path $package) -and @(Get-ChildItem $package -Force).Count){throw 'Output directory is not empty; choose -OutputDirectory to preserve the previous package'}
New-Item -ItemType Directory -Force $package|Out-Null
$source=@(New-OwySourceArchive $ProjectRoot "$package/OWY-source.zip")
& $dotnet publish src/OWY.App/OWY.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $package -v minimal
if($LASTEXITCODE){throw 'WinUI app build failed'}
Assert-OwySourceUnchanged $ProjectRoot $source
New-Item -ItemType Directory -Force "$package\python"|Out-Null
Copy-Item "$python\*" "$package\python" -Recurse -Force
[System.IO.File]::WriteAllText("$package\python\python313._pth","python313.zip`r`n.`r`n..\worker`r`n",[System.Text.Encoding]::ASCII)
New-Item -ItemType Directory -Force "$package\worker\tokenscope"|Out-Null
Copy-Item worker/owy_worker.py,worker/configure_telemetry.py "$package\worker" -Force
Copy-Item worker/tokenscope/live_probe.py,worker/tokenscope/live_meter.py,worker/tokenscope/live_telemetry.py,worker/tokenscope/LICENSE,worker/tokenscope/PROVENANCE.json "$package\worker\tokenscope" -Force
Copy-Item LICENSE,LICENSE-EXCEPTION,COPYRIGHT,LICENSING.md,CONTRIBUTING.md,README.md,THIRD_PARTY_NOTICES.md,THIRD_PARTY_TERMS.md $package -Force
Copy-Item scripts/install.ps1,scripts/release.ps1 $package -Force
New-Item -ItemType Directory -Force "$package\assets"|Out-Null
Copy-Item assets/OWY.ico "$package\assets" -Force
Copy-Item assets/owy-owl.png "$package\assets" -Force
Copy-Item docs $package -Recurse -Force
New-Item -ItemType Directory -Force "$package\licenses"|Out-Null;Copy-Item licenses/* "$package\licenses" -Recurse -Force
$info=[pscustomobject]@{schema_version=1;built_at_utc=[DateTime]::UtcNow.ToString('o');source_archive='OWY-source.zip';source_sha256=(Get-FileHash "$package/OWY-source.zip" -Algorithm SHA256).Hash.ToLowerInvariant();source_files=$source.Count;dotnet_sdk=(& $dotnet --version);self_contained=$true;target='win-x64'}
[IO.File]::WriteAllText("$package/release-info.json",($info|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
$manifest=@(Get-ChildItem $package -File -Recurse|Where-Object {$_.FullName -ne "$package\manifest.json"}|ForEach-Object{[pscustomobject]@{file=$_.FullName.Substring($package.Length+1);bytes=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
[System.IO.File]::WriteAllText("$package\manifest.json",($manifest|ConvertTo-Json -Depth 4),(New-Object System.Text.UTF8Encoding($false)))
Test-OwyRelease $package
Write-Output "Self-contained package built: $package"
