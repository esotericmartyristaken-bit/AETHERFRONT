$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work=$PSScriptRoot
$archive=[IO.Path]::GetFullPath((Join-Path $work '../client-artifacts/Aetherfront-RC22/Aetherfront-Starter-rc22.zip'))
$fixture=Join-Path $work ('StarterFixture-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
[IO.Compression.ZipFile]::ExtractToDirectory($archive,$fixture)
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{
    foreach($entry in $zip.Entries){
        if(-not $seen.Add($entry.FullName)){throw 'Duplicate starter file.'}
        if($entry.FullName -match '(?i)(\.exe$|\.cmd$|KesasLauncher\.bin$|privatekey|apfhelper.key|\.pfx$|update-signing-private|KesasInstallProfile|KesasInstallSelection|KESASGraphicsBeforePotato|(^|/)saves/)'){throw "Unexpected executable, player state or private key: $($entry.FullName)"}
    }
    foreach($required in @('READ ME FIRST.txt','GameData/000_Harmony/0Harmony.dll','GameData/LunaMultiplayer/Plugins/LmpClient.dll','GameData/LunaMultiplayer/Plugins/ApfCore.dll','GameData/LunaMultiplayer/Plugins/KesasInstall.dll','GameData/LunaMultiplayer/Plugins/AetherfrontCore.dll','GameData/LunaMultiplayer/Plugins/CachedQuickLz.dll','GameData/LunaMultiplayer/Plugins/JsonFx.dll','GameData/LunaMultiplayer/Plugins/Lidgren.Network.dll','GameData/LunaMultiplayer/Plugins/System.Memory.dll','GameData/LunaMultiplayer/Plugins/System.Buffers.dll','GameData/LunaMultiplayer/Plugins/System.Numerics.Vectors.dll','GameData/LunaMultiplayer/Plugins/System.Runtime.CompilerServices.Unsafe.dll','GameData/LunaMultiplayer/APFApplyUpdate.ps1','GameData/LunaMultiplayer/Launcher/install-catalog.json','GameData/LunaMultiplayer/Data/APFServer.txt','GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt','GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml','KSP_x64_Data/Managed/System.IO.Compression.dll','KSP_x64_Data/Managed/System.IO.Compression.FileSystem.dll','KSP_x64_Data/Managed/System.Numerics.dll')){if(-not $zip.GetEntry($required)){throw "Missing dependency: $required"}}
    $manifest=Get-Content -LiteralPath "$work/Candidate/Published/APFClientUpdate.json" -Raw|ConvertFrom-Json
    $signed=@{};foreach($file in $manifest.files){$signed[$file.path]=$file.sha256}
    foreach($file in $manifest.files){if(-not $zip.GetEntry($file.path)){throw "Signed client file missing from starter: $($file.path)"}}
    foreach($entry in $zip.Entries){if($signed.ContainsKey($entry.FullName)){$sha=[Security.Cryptography.SHA256]::Create();$stream=$entry.Open();try{$actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()};if($actual -ne $signed[$entry.FullName]){throw "Starter differs from signed release: $($entry.FullName)"}}}
    Write-Output "PASS $($zip.Entries.Count) starter entries: all client/runtime dependencies, public trust and profile catalog; no manual launcher, private keys or player state."
}finally{$zip.Dispose()}
$catalog=Get-Content -LiteralPath (Join-Path $fixture 'GameData/LunaMultiplayer/Launcher/install-catalog.json') -Raw|ConvertFrom-Json
$old=Get-Content -LiteralPath "$work/../KESAS-RC16-Work/install-catalog.json" -Raw|ConvertFrom-Json
foreach($profile in @('full','potato')){
    if($catalog.$profile.requiredClientVersion -ne 'kesas-rc22' -or $catalog.$profile.packVersion -ne 'kesas-rc22'){throw 'Catalog version mismatch.'}
    if(($catalog.$profile.managedPackages|ConvertTo-Json -Depth 20 -Compress) -cne ($old.$profile.managedPackages|ConvertTo-Json -Depth 20 -Compress)){throw 'Gameplay/media records changed unexpectedly.'}
}
Write-Output 'PASS Regular 210 and Potato 176 package records are unchanged from validated RC16 media.'
foreach($name in @('JsonFx.dll','ApfCore.dll','KesasInstall.dll')){[IO.File]::WriteAllText((Join-Path $fixture ('GameData/LunaMultiplayer/Plugins/'+$name))+':Zone.Identifier',"[ZoneTransfer]`r`nZoneId=3`r`n")}
$null=New-Item -ItemType Directory -Path (Join-Path $fixture 'GameData/Squad')
Copy-Item -LiteralPath "$work/GameStub/bin/Release/net472/GameStub.exe" -Destination (Join-Path $fixture 'KSP_x64.exe')
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell/v1.0/powershell.exe'
$start.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $work 'Probe-Starter.ps1')+'" -GameRoot "'+$fixture+'"'
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start);$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
if(-not $process.WaitForExit(45000)){$process.Kill();throw 'Owned starter probe timed out.'}
if($process.ExitCode -ne 0){throw ($stderr.Result+$stdout.Result)}
Write-Output $stdout.Result.Trim()
$sha=(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()
if(-not ([IO.File]::ReadAllText("$archive.sha256").StartsWith($sha+'  '))){throw 'Starter checksum mismatch.'}
Write-Output "PASS starter SHA256 $sha"
