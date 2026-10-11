# Shared by the build, installer and release regression checks. Dot-source only.
Set-StrictMode -Version Latest

function Get-OwySourceFiles([string]$Root) {
    $top=@('.gitattributes','.gitignore','Directory.Build.props','global.json','README.md',
        'LICENSE','LICENSE-EXCEPTION','COPYRIGHT','LICENSING.md','CONTRIBUTING.md',
        'THIRD_PARTY_NOTICES.md','THIRD_PARTY_TERMS.md')
    $files=@($top|ForEach-Object {Get-Item -LiteralPath (Join-Path $Root $_) -ErrorAction Stop})
    foreach($folder in @('src','worker','tests','scripts','licenses','assets','docs')) {
        $files+=@(Get-ChildItem -LiteralPath (Join-Path $Root $folder) -File -Recurse -Force|Where-Object {
            $_.FullName.Substring($Root.Length+1) -notmatch '(^|[\\/])(bin|obj|__pycache__|build|dist)([\\/]|$)' -and
            $_.Name -ne '.DS_Store' -and !$_.Name.StartsWith('._') -and $_.Extension -notin @('.pyc','.user','.suo')
        })
    }
    foreach($file in $files) {
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Source archive cannot contain links'}
        if($file.Name -match '^\.env($|\.)' -or $file.Extension -in @('.pfx','.p12','.key','.pem','.db','.sqlite','.sqlite3','.exe','.dll')) {
            throw ('Private or binary file in public source tree: '+$file.Name)
        }
    }
    return @($files|Sort-Object FullName)
}

function Assert-OwyDependencyLicenses([string]$Root) {
    $inventory=Get-Content -LiteralPath "$Root/licenses/nuget-dependencies.json" -Raw|ConvertFrom-Json
    if($inventory.schema_version -ne 1){throw 'Unsupported dependency inventory'}
    $locked=@{}
    foreach($lock in Get-ChildItem "$Root/src","$Root/tests" -Filter packages.lock.json -Recurse -File) {
        $json=Get-Content $lock.FullName -Raw|ConvertFrom-Json
        foreach($framework in $json.dependencies.PSObject.Properties) {
            foreach($entry in $framework.Value.PSObject.Properties) {
                if($entry.Value.type -eq 'Project'){continue}
                $locked[$entry.Name+'/'+$entry.Value.resolved]=$true
            }
        }
    }
    $documented=@{}
    foreach($package in $inventory.packages) {
        $identity=$package.id+'/'+$package.version
        if($documented.ContainsKey($identity)){throw ('Duplicate dependency: '+$identity)}
        $documented[$identity]=$true
        if($package.role -ne 'build-only' -and @($package.files).Count -eq 0){throw ('No licence files for '+$identity)}
        foreach($file in $package.files) {
            $path=Join-Path "$Root/licenses" $file.path
            if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
                throw ('Missing or altered dependency notice: '+$file.path)
            }
        }
    }
    if(Compare-Object @($locked.Keys|Sort-Object) @($documented.Keys|Sort-Object)){throw 'Locked dependencies differ from the reviewed licence inventory'}
    $provenance=Get-Content "$Root/worker/tokenscope/PROVENANCE.json" -Raw|ConvertFrom-Json
    foreach($item in $provenance.sha256.PSObject.Properties){
        $path=Join-Path "$Root/worker/tokenscope" $item.Name
        if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.Value){throw ('Collector provenance mismatch: '+$item.Name)}
    }
}

function New-OwySourceArchive([string]$Root,[string]$Archive) {
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $records=@(Get-OwySourceFiles $Root|ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($Root.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $zip=[IO.Compression.ZipFile]::Open($Archive,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($item in $records){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $Root $item.path),$item.path)|Out-Null}
        $entry=$zip.CreateEntry('SOURCE-MANIFEST.json')
        $writer=New-Object IO.StreamWriter($entry.Open(),(New-Object Text.UTF8Encoding($false)))
        try{$writer.Write((ConvertTo-Json -InputObject @($records) -Depth 4))}finally{$writer.Dispose()}
    }finally{$zip.Dispose()}
    return $records
}

function Assert-OwySourceUnchanged([string]$Root,$Before) {
    $after=@(Get-OwySourceFiles $Root)
    if($after.Count -ne @($Before).Count){throw 'Public source changed during the build; rebuild from a stable tree'}
    foreach($item in $Before){
        $path=Join-Path $Root $item.path
        if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.sha256){throw ('Source changed during the build: '+$item.path)}
    }
}

function Test-OwyRelease([string]$Package) {
    $Package=[IO.Path]::GetFullPath($Package).TrimEnd('\','/')
    $items=Get-Content -LiteralPath "$Package/manifest.json" -Raw|ConvertFrom-Json
    $seen=@{}
    foreach($item in $items) {
        $path=[IO.Path]::GetFullPath((Join-Path $Package $item.file))
        if(!$path.StartsWith($Package+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($path)){throw 'Invalid or duplicate package path'}
        $seen[$path]=$true
        if(!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.sha256){throw ('Package checksum failed: '+$item.file)}
    }
    foreach($required in @('OWY.exe','LICENSE','LICENSE-EXCEPTION','THIRD_PARTY_NOTICES.md','THIRD_PARTY_TERMS.md',
        'licenses/nuget-dependencies.json','python/LICENSE.txt','worker/tokenscope/LICENSE','OWY-source.zip','release-info.json','install.ps1','release.ps1')) {
        if(!$seen.ContainsKey([IO.Path]::GetFullPath((Join-Path $Package $required)))){throw ('Required package file missing: '+$required)}
    }
    $inventory=Get-Content "$Package/licenses/nuget-dependencies.json" -Raw|ConvertFrom-Json
    foreach($dependency in $inventory.packages){foreach($notice in $dependency.files){
        if(!$seen.ContainsKey([IO.Path]::GetFullPath((Join-Path "$Package/licenses" $notice.path)))){throw ('Unpackaged dependency notice: '+$notice.path)}
        if((Get-FileHash -LiteralPath (Join-Path "$Package/licenses" $notice.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $notice.sha256){throw ('Dependency notice does not match inventory: '+$notice.path)}
    }}
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $info=Get-Content "$Package/release-info.json" -Raw|ConvertFrom-Json
    if((Get-FileHash "$Package/OWY-source.zip" -Algorithm SHA256).Hash.ToLowerInvariant() -ne $info.source_sha256){throw 'Source archive does not match this build'}
    $zip=[IO.Compression.ZipFile]::OpenRead("$Package/OWY-source.zip")
    try {
        $reader=New-Object IO.StreamReader($zip.GetEntry('SOURCE-MANIFEST.json').Open())
        try{$source=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
        if($zip.Entries.Count -ne $source.Count+1){throw 'Unexpected source archive entries'}
        foreach($item in $source){
            if($item.path -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|\\)'){throw 'Unsafe source archive path'}
            $entry=$zip.GetEntry($item.path);if($null -eq $entry){throw ('Source archive file missing: '+$item.path)}
            $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
            try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}
            if($hash -ne $item.sha256){throw ('Source checksum failed: '+$item.path)}
        }
        foreach($required in @('src/OWY.App/OWY.App.csproj','src/OWY.App/App.xaml.cs','Directory.Build.props','global.json',
            'scripts/build.ps1','scripts/bootstrap.ps1','scripts/install.ps1','scripts/release.ps1','LICENSE','LICENSE-EXCEPTION',
            'worker/owy_worker.py','worker/tokenscope/live_probe.py','assets/OWY.ico','licenses/nuget-dependencies.json')) {
            if($null -eq $zip.GetEntry($required)){throw ('Required corresponding source missing: '+$required)}
        }
    }finally{$zip.Dispose()}
}

function Get-OwyRuntimeTerms([string]$Package) {
    $paths=@('THIRD_PARTY_TERMS.md','licenses/microsoft.windowsappsdk-2.5.1-license.txt',
        'licenses/microsoft.windows.ai.machinelearning-2.1.74-license.txt','licenses/microsoft.windowsappsdk.ml-2.1.94-license.txt')
    return @($paths|ForEach-Object {
        $path=Join-Path $Package $_
        [pscustomobject]@{file=$_;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();text=(Get-Content -LiteralPath $path -Raw)}
    })
}

function Confirm-OwyRuntimeTerms($Terms,[bool]$Accepted) {
    if(!$Accepted){
        Write-Host 'The bundled Microsoft runtimes have separate terms. OWY retains its AGPL rights.'
        Write-Host 'Review THIRD_PARTY_TERMS.md and its linked licence files. Use -ShowThirdPartyTerms to print them.'
        Write-Host 'Declining installs nothing.'
        if((Read-Host 'Type ACCEPT if you have reviewed and agree to the applicable runtime terms') -cne 'ACCEPT'){throw 'Installation cancelled; runtime terms were not accepted'}
    }
    return [pscustomobject]@{schema_version=1;accepted_at_utc=[DateTime]::UtcNow.ToString('o');documents=@($Terms|Select-Object file,sha256)}
}
