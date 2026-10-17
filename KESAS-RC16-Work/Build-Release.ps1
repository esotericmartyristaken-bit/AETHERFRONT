$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root='.\KESAS-VPS-Server'
$work=$PSScriptRoot
& (Join-Path $work '../tools/dotnet-sdk8/dotnet.exe') build "$work/Launcher/Launcher.csproj" -c Release --nologo -v quiet
if($LASTEXITCODE -ne 0){throw 'Launcher build failed.'}
$release=Join-Path $work 'Release'
if(Test-Path -LiteralPath $release){throw 'Release already exists. Use a new output directory for another candidate.'}
$null=New-Item -ItemType Directory -Path $release
$utf8=[Text.UTF8Encoding]::new($false)
$visual=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($line in Get-Content -LiteralPath "$work/visual-roots.txt"){if($line.Trim()){$null=$visual.Add($line.Trim())}}
$old=[IO.Compression.ZipFile]::OpenRead("$root/ClientTemplate.zip")
$new=[IO.Compression.ZipFile]::Open("$release/ClientTemplate.zip",[IO.Compression.ZipArchiveMode]::Create)
function Put-Entry([string]$name,[byte[]]$bytes){$entry=$new.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal);$s=$entry.Open();try{$s.Write($bytes,0,$bytes.Length)}finally{$s.Dispose()}}
try{
    $inventory=$null
    foreach($entry in $old.Entries){
        $name=$entry.FullName
        if($name -match '^GameData/([^/]+)(/|$)' -and $visual.Contains($Matches[1])){continue}
        if($name -in @('GameData/LunaMultiplayer/Plugins/LmpClient.dll','GameData/LunaMultiplayer/Plugins/ApfCore.dll','GameData/LunaMultiplayer/Data/APFClient.json','KESAS/PLAYER-INSTALL.txt')){continue}
        $s=$entry.Open();$m=[IO.MemoryStream]::new();try{$s.CopyTo($m);$bytes=$m.ToArray()}finally{$s.Dispose();$m.Dispose()}
        if($name -eq 'KESAS/PATCH-MANIFEST.json'){$inventory=$utf8.GetString($bytes)|ConvertFrom-Json;continue}
        if($name -eq 'GameData/LunaMultiplayer/APFApplyUpdate.ps1'){
            $text=$utf8.GetString($bytes)
            $copy='[IO.File]::Copy($source, $destination, $false)'
            if(-not $text.Contains($copy)){throw 'Unexpected post-exit helper copy code.'}
            $bytes=$utf8.GetBytes($text.Replace($copy,'[IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($source))'))
        }
        if($name -eq 'KESAS/Notices/FULL-PACK-POLICY.json'){
            $policy=$utf8.GetString($bytes)|ConvertFrom-Json
            $policy.version='kesas-rc16';$policy.managedDownloads=$true;$policy.note='Full and Potato installs download from the KESAS server. Shared gameplay packages are identical.'
            $bytes=$utf8.GetBytes(($policy|ConvertTo-Json -Depth 30)+"`n")
        }
        Put-Entry $name $bytes
    }
    Put-Entry 'GameData/LunaMultiplayer/Plugins/LmpClient.dll' ([IO.File]::ReadAllBytes("$work/LmpClient.dll"))
    Put-Entry 'GameData/LunaMultiplayer/Plugins/ApfCore.dll' ([IO.File]::ReadAllBytes("$work/ApfCore.dll"))
    Put-Entry 'GameData/LunaMultiplayer/Data/APFClient.json' ($utf8.GetBytes('{"enabled":true,"clientVersion":"kesas-rc16"}'+"`n"))
    foreach($item in @(
        @{source='Launcher/bin/Release/net472/KesasLauncher.dll';target='GameData/LunaMultiplayer/Launcher/KesasLauncher.bin'},
        @{source='install-catalog.json';target='GameData/LunaMultiplayer/Launcher/install-catalog.json'},
        @{source='visual-roots.txt';target='GameData/LunaMultiplayer/Launcher/visual-roots.txt'},
        @{source='KESASLauncher.ps1';target='GameData/LunaMultiplayer/KESASLauncher.ps1'},
        @{source='Launcher/Launch KESAS.cmd';target='GameData/LunaMultiplayer/Launcher/Launch KESAS.cmd'},
        @{source='PLAYER-INSTALL.txt';target='KESAS/PLAYER-INSTALL.txt'}
    )){Put-Entry $item.target ([IO.File]::ReadAllBytes((Join-Path $work $item.source)))}
    $registry=Get-Content -LiteralPath "$work/ModpackSource/CKAN/registry.json" -Raw|ConvertFrom-Json
    $sources=@($registry.installed_modules.PSObject.Properties | ForEach-Object {
        $module=$_.Value.source_module
        if($module.identifier -ne 'KESAS-Multiplayer-RC13-Full') {
            $record=[ordered]@{}
            foreach($key in @('identifier','version','author','license','resources','download','download_hash')) {
                if($module.PSObject.Properties[$key]){$record[$key]=$module.$key}
            }
            $record
        }
    })
    $notice=[ordered]@{version='kesas-rc16';baselineSha256='3a89bed6574936259ed3964b6918d618fe553d5b82a6704e41d609e936ce6ed5';modules=$sources;binaryPinAdjustments=(Get-Content -LiteralPath "$work/source-pin-adjustments.json" -Raw|ConvertFrom-Json);note='Mod licenses remain in their original package folders. Paid cloud assets are hosted with operator-confirmed redistribution permission. Potato keeps unmodified Waterfall API dependencies and engine configuration rules, but removes plume assets and effects.'}
    Put-Entry 'KESAS/Notices/PACK-SOURCES-rc16.json' ($utf8.GetBytes(($notice|ConvertTo-Json -Depth 30)+"`n"))
}finally{$old.Dispose();$new.Dispose()}
$zip=[IO.Compression.ZipFile]::Open("$release/ClientTemplate.zip",[IO.Compression.ZipArchiveMode]::Update)
try{
    $records=foreach($entry in $zip.Entries){
        $length=$entry.Length
        $sha=[Security.Cryptography.SHA256]::Create();$s=$entry.Open()
        try{$hash=[BitConverter]::ToString($sha.ComputeHash($s)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$s.Dispose()}
        [ordered]@{path=$entry.FullName;bytes=$length;sha256=$hash}
    }
    $inventory.version='kesas-rc16';$inventory.files=@($records)
    $s=$zip.CreateEntry('KESAS/PATCH-MANIFEST.json').Open();$writer=[IO.StreamWriter]::new($s,$utf8)
    try{$writer.Write(($inventory|ConvertTo-Json -Depth 30)+"`n")}finally{$writer.Dispose()}
}finally{$zip.Dispose()}
$pack=Get-Content -LiteralPath "$root/Server/Config/APFPack.json" -Raw|ConvertFrom-Json
if($pack.requiredClientVersion -cne 'kesas-rc15'){throw 'Active version changed during this build.'}
$pack.requiredClientVersion='kesas-rc16';$pack.packVersion='kesas-rc16'
$pack.clientOnly+=([pscustomobject]@{id='kesas-potato-policy';pathRoots=@('GameData/ZZZ_KESASPotato');dlls=@()})
$adjustments=Get-Content -LiteralPath "$work/source-pin-adjustments.json" -Raw|ConvertFrom-Json
foreach($item in $adjustments){foreach($rule in $pack.clientOnly){foreach($dll in $rule.dlls){if($dll.path -ceq $item.path){if($dll.sha256 -cne $item.oldSha256){throw 'Expected pin changed during preparation.'};$dll.sha256=$item.sha256}}}}
[IO.File]::WriteAllText("$release/APFPack-template.json",($pack|ConvertTo-Json -Depth 40)+"`n",$utf8)
Copy-Item -LiteralPath "$root/Server/Config/AetherfrontTechnology.json" -Destination $release
$names=@('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')
$files=@($names|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $release $_)).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText("$release/RELEASE.json",([ordered]@{version='kesas-rc16';files=$files}|ConvertTo-Json -Depth 10)+"`n",$utf8)
$archive=[IO.Compression.ZipFile]::Open("$work/KESAS-Release-kesas-rc16.zip",[IO.Compression.ZipArchiveMode]::Create)
try{foreach($name in ($names+@('RELEASE.json'))){$null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $release $name),$name,[IO.Compression.CompressionLevel]::Optimal)}}finally{$archive.Dispose()}
Write-Output "Built RC16: $($records.Count) signed-overlay files; graphical patches are supplied only through the Full profile."
