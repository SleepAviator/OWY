param([string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\','/')
. "$ProjectRoot/scripts/release.ps1"
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('OWY-release-tests-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $scratch 'source';$package=Join-Path $scratch 'package';$script:checks=0
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Expected){
    $failure=$null;try{& $Action|Out-Null}catch{$failure=$_.Exception.Message}
    Check ($null -ne $failure -and $failure -like ('*'+$Expected+'*')) ('Expected rejection: '+$Expected+'; got '+$failure)
}
function Save-PackageManifest {
    $entries=@(Get-ChildItem $package -File -Recurse|Where-Object {$_.FullName -ne "$package\manifest.json"}|ForEach-Object {
        [pscustomobject]@{file=$_.FullName.Substring($package.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    [IO.File]::WriteAllText("$package/manifest.json",(ConvertTo-Json -InputObject $entries -Depth 4))
}
try {
    New-Item -ItemType Directory $source,$package -Force|Out-Null
    foreach($file in Get-OwySourceFiles $ProjectRoot){
        $target=Join-Path $source $file.FullName.Substring($ProjectRoot.Length+1)
        New-Item -ItemType Directory (Split-Path $target -Parent) -Force|Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    Assert-OwyDependencyLicenses $source;$script:checks++
    $records=@(New-OwySourceArchive $source "$package/OWY-source.zip")
    Assert-OwySourceUnchanged $source $records;$script:checks++
    $cs=Join-Path $source 'src/OWY.App/App.xaml.cs';$original=[IO.File]::ReadAllText($cs)
    [IO.File]::AppendAllText($cs,"`n// Changed after snapshot")
    Reject {Assert-OwySourceUnchanged $source $records} 'Source changed during the build'
    [IO.File]::WriteAllText($cs,$original)
    $inventory=Get-Content "$source/licenses/nuget-dependencies.json" -Raw|ConvertFrom-Json
    $notice=Join-Path "$source/licenses" $inventory.packages[0].files[0].path
    $originalBytes=[IO.File]::ReadAllBytes($notice);[IO.File]::AppendAllText($notice,'tampered')
    Reject {Assert-OwyDependencyLicenses $source} 'altered dependency notice'
    [IO.File]::WriteAllBytes($notice,$originalBytes)
    $lock="$source/src/OWY.App/packages.lock.json";$lockBytes=[IO.File]::ReadAllBytes($lock)
    $json=Get-Content $lock -Raw|ConvertFrom-Json
    $first=@($json.dependencies.PSObject.Properties)[0].Value
    $first.'Microsoft.WindowsAppSDK'.resolved='999.0.0'
    [IO.File]::WriteAllText($lock,($json|ConvertTo-Json -Depth 20))
    Reject {Assert-OwyDependencyLicenses $source} 'Locked dependencies differ'
    [IO.File]::WriteAllBytes($lock,$lockBytes)
    $collector="$source/worker/tokenscope/live_meter.py";$collectorBytes=[IO.File]::ReadAllBytes($collector)
    [IO.File]::AppendAllText($collector,"`n# Unrecorded change")
    Reject {Assert-OwyDependencyLicenses $source} 'Collector provenance mismatch'
    [IO.File]::WriteAllBytes($collector,$collectorBytes)
    Copy-Item "$source/licenses" $package -Recurse
    Copy-Item "$source/LICENSE","$source/LICENSE-EXCEPTION","$source/THIRD_PARTY_NOTICES.md","$source/THIRD_PARTY_TERMS.md" $package
    Copy-Item "$source/scripts/install.ps1","$source/scripts/release.ps1" $package
    New-Item -ItemType Directory "$package/python","$package/worker/tokenscope" -Force|Out-Null
    [IO.File]::WriteAllText("$package/OWY.exe",'Synthetic package fixture; never executed')
    [IO.File]::WriteAllText("$package/python/LICENSE.txt",'Synthetic runtime fixture')
    Copy-Item "$source/worker/tokenscope/LICENSE" "$package/worker/tokenscope/LICENSE"
    [IO.File]::WriteAllText("$package/release-info.json",([pscustomobject]@{source_sha256=(Get-FileHash "$package/OWY-source.zip" -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json))
    Save-PackageManifest
    Test-OwyRelease $package;$script:checks++
    $before=@(Get-ChildItem $package -File -Recurse).Count
    & "$package/install.ps1" -Package $package -VerifyOnly|Out-Null
    Check (@(Get-ChildItem $package -File -Recurse).Count -eq $before) 'Verify-only changed package'
    Check (!(Test-Path "$package/runtime-terms-acceptance.json")) 'Verify-only recorded acceptance'
    $license=Join-Path "$package/licenses" $inventory.packages[0].files[0].path
    [IO.File]::AppendAllText($license,'changed');Save-PackageManifest
    Reject {Test-OwyRelease $package} 'Dependency notice does not match inventory'
    [IO.File]::WriteAllBytes($license,$originalBytes);Save-PackageManifest
    Rename-Item "$package/OWY-source.zip" 'source-hidden.zip';Save-PackageManifest
    Reject {Test-OwyRelease $package} 'Required package file missing'
    Rename-Item "$package/source-hidden.zip" 'OWY-source.zip';Save-PackageManifest
    $bad=(Get-Content "$package/manifest.json" -Raw|ConvertFrom-Json)+[pscustomobject]@{file='../escape.txt';sha256='bad'}
    [IO.File]::WriteAllText("$package/manifest.json",(ConvertTo-Json -InputObject $bad -Depth 4))
    Reject {Test-OwyRelease $package} 'Invalid or duplicate package path'
    Save-PackageManifest
    $zip=[IO.Compression.ZipFile]::Open("$package/OWY-source.zip",[IO.Compression.ZipArchiveMode]::Update)
    try{$zip.GetEntry('src/OWY.App/App.xaml.cs').Delete()}finally{$zip.Dispose()}
    [IO.File]::WriteAllText("$package/release-info.json",([pscustomobject]@{source_sha256=(Get-FileHash "$package/OWY-source.zip" -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json))
    Save-PackageManifest
    Reject {Test-OwyRelease $package} 'Unexpected source archive entries'
    function Read-Host {param($Prompt);return 'NO'}
    $fakeTerms=@([pscustomobject]@{file='TEST-ONLY';sha256=('0'*64);text='Synthetic consent fixture; not an agreement'})
    Reject {Confirm-OwyRuntimeTerms $fakeTerms $false} 'Installation cancelled'
    $receipt=Confirm-OwyRuntimeTerms $fakeTerms $true
    Check ($receipt.documents.Count -eq 1 -and $receipt.documents[0].file -eq 'TEST-ONLY') 'Explicit test consent did not retain document identity'
    Check ($receipt.PSObject.Properties.Name -notcontains 'user') 'Acceptance receipt contains unnecessary identity'
    Write-Output ("Release packaging checks passed: "+$script:checks)
}finally{
    if(Test-Path $scratch){Remove-Item -LiteralPath $scratch -Recurse -Force}
}
