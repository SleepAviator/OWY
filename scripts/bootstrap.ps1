param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$tools = Join-Path $ProjectRoot '.tools'
$dotnet = Join-Path $tools 'dotnet'
$exe = Join-Path $dotnet 'dotnet.exe'
if (!(Test-Path $exe)) {
    New-Item -ItemType Directory -Force $tools | Out-Null
    $zip = Join-Path $tools 'dotnet-sdk-10.0.401-win-x64.zip'
    Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip' -OutFile $zip -UseBasicParsing
    $expected = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
    if ((Get-FileHash $zip -Algorithm SHA512).Hash.ToLowerInvariant() -ne $expected) { throw 'Official .NET SDK checksum mismatch' }
    Expand-Archive $zip -DestinationPath $dotnet
    Remove-Item $zip
}
& $exe --version
if ($LASTEXITCODE) { throw 'The .NET SDK did not start' }
