#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$workspace='.'
$source="$workspace/Aetherfront-RC28-Work/SettingsTests/Attempts/25a1f489ef70/CLR-migration27/F5149caa2"
$expectedEngine='634fd678526864009aaf3c365a377409d2f031d9daf03e48f208dc89278f3ba1'
if (@(Get-Process -Name KSP,KSP_x64 -ErrorAction SilentlyContinue).Count) { throw 'Wait for all KSP fixture processes to exit.' }
$clone=Join-Path $workspace ('P8/m'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$source=[IO.Path]::GetFullPath($source);$clone=[IO.Path]::GetFullPath($clone)
if(!(Test-Path -LiteralPath "$source/startup-result.json") -or (Get-Content -LiteralPath "$source/startup-result.json" -Raw) -cne 'passed'){throw 'Passed source fixture required.'}
if((Get-FileHash -LiteralPath "$source/GameData/LunaMultiplayer/Plugins/KesasInstall.dll" -Algorithm SHA256).Hash -ine $expectedEngine){throw 'Unexpected Engine.'}
$plugins="$source/GameData/LunaMultiplayer/Plugins"
foreach($name in @('JsonFx','ApfCore','KesasInstall')){[void][Reflection.Assembly]::LoadFrom("$plugins/$name.dll")}
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$deserialize=[ApfCore.ApfJson].GetMethods() | Where-Object {$_.Name -eq 'Deserialize' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1}
function Read-Transaction([string]$text){$deserialize.MakeGenericMethod([type[]]@([ApfCore.ApfSyncTransaction])).Invoke($null,[object[]]@($text))}
$read=[ApfCore.ApfRecoveryStore].GetMethod('Read',$flags,$null,[type[]]@([string],[string]),$null)
$receiptRead=[KesasInstall.Installer].GetMethod('ReadCompletedReceipt',$flags)
if(!$read -or !$receiptRead.Invoke($null,[object[]]@($source))){throw 'Source receipt/recovery API invalid.'}
$journals=@(Get-ChildItem -LiteralPath "$source/APF/.transaction-backup" -Filter recovery.json -Recurse -File)
foreach($file in $journals){[void]$read.Invoke($null,[object[]]@($source,$file.FullName))}
[void][IO.Directory]::CreateDirectory($clone)
& robocopy $source $clone /E /R:0 /W:0 /COPY:DAT /DCOPY:T /NFL /NDL /NJH /NJS /NP
if($LASTEXITCODE -gt 7){throw 'Private clone failed.'}
# CLR finds compression in the GAC; the actual Mono host needs the shipped 4.0
# dependencies beside this four-file synthetic client. Verify their signed baseline.
$historical="$workspace/KESAS-VPS-Server/Updates/kesas-rc27"
& "$workspace/KESAS-VPS-Server/UpdateTool/ApfUpdateTool.exe" --verify --manifest "$historical/APFClientUpdate.json" --archive "$historical/APFClient-kesas-rc27.zip" --public-key-xml "$workspace/KESAS-VPS-Server/secrets/update-signing-public.xml" *> "$clone/dependency-authentication.log"
if($LASTEXITCODE -ne 0){throw 'Signed dependency baseline verification failed.'}
$dependencyFeed=Get-Content -LiteralPath "$historical/APFClientUpdate.json" -Raw|ConvertFrom-Json
foreach($name in @('System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')){
    $relative="KSP_x64_Data/Managed/$name";$declared=@($dependencyFeed.files|Where-Object {$_.path -ceq $relative})
    $dependency="$workspace/Aetherfront-RC28-Work/BuildAttempts/Engine-b41be162d2184d98839be6d25ba55629/Baseline/$relative"
    if($declared.Count -ne 1 -or (Get-FileHash -LiteralPath $dependency -Algorithm SHA256).Hash -ine $declared[0].sha256){throw 'Compression dependency differs from signed release.'}
    [void][IO.Directory]::CreateDirectory("$clone/KSP_x64_Data/Managed");Copy-Item -LiteralPath $dependency -Destination "$clone/$relative"
}
function Rebase([string]$path){if(!$path.StartsWith($source,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected non-fixture path.'};$clone+$path.Substring($source.Length)}
$key=[ApfCore.ApfTransactionSecurity]::LoadKey("$clone/GameData/LunaMultiplayer/Data/APFHelper.key")
foreach($file in $journals){
    $value=$read.Invoke($null,[object[]]@($source,$file.FullName));$value.GameRoot=$clone
    foreach($record in $value.Records){$record.Target=Rebase $record.Target;if($record.Existed){$record.Backup=Rebase $record.Backup}}
    $directory=Split-Path (Rebase $file.FullName)
    [ApfCore.ApfRecoveryStore]::Save($directory,$value)
    [void]$read.Invoke($null,[object[]]@($clone,"$directory/recovery.json"))
}
$transactions=@(Get-ChildItem -LiteralPath "$clone/APF/.staging/connect" -Filter transaction.json.consumed -Recurse -File)
foreach($file in $transactions){
    $value=Read-Transaction ([IO.File]::ReadAllText($file.FullName))
    if(![ApfCore.ApfTransactionSecurity]::Verify($value,$key)){throw 'Original consumed transaction HMAC invalid.'}
    $value.GameRoot=$clone;$value.GameDataPath=Rebase $value.GameDataPath;$value.StatePath=Rebase $value.StatePath;$value.KspExecutable=Rebase $value.KspExecutable
    foreach($package in $value.Packages){$package.ArchivePath=Rebase $package.ArchivePath}
    [ApfCore.ApfTransactionSecurity]::Sign($value,$key)
    [IO.File]::WriteAllText($file.FullName,[ApfCore.ApfJson]::Serialize($value))
}
$paths=[KesasInstall.UserSettings]::Paths
$transaction=Read-Transaction ([IO.File]::ReadAllText($transactions[0].FullName))
foreach($relative in $paths){
    $staged=@($transaction.Packages | Where-Object {$relative.StartsWith($_.Package.StripPrefix+'/',[StringComparison]::OrdinalIgnoreCase)})
    if($staged.Count -ne 1){throw 'Settings archive not unique.'}
    if((Get-FileHash -LiteralPath $staged[0].ArchivePath -Algorithm SHA256).Hash -ine $staged[0].Package.Sha256){throw 'Archive hash invalid.'}
    $zip=[IO.Compression.ZipFile]::OpenRead($staged[0].ArchivePath)
    try{
        $entry=@($zip.Entries | Where-Object {$_.FullName.Replace('\','/').Equals($relative,[StringComparison]::OrdinalIgnoreCase)})
        if($entry.Count -gt 1){throw 'Duplicate signed setting default.'}
        $target=Join-Path "$clone/GameData" $relative
        if($entry.Count){$input=$entry[0].Open();$output=[IO.File]::Create($target);try{$input.CopyTo($output)}finally{$output.Dispose();$input.Dispose()}}
        elseif(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target}
    }finally{$zip.Dispose()}
}
Remove-Item -LiteralPath "$clone/APF/LegacySettingsRecovery.json"
# Reuse the already-compiled production startup test; no code compilation. Preserve
# copied run artifacts under APF so its one-launch assertions can run independently.
$preserved="$clone/APF/mono-probe-preserved";[void][IO.Directory]::CreateDirectory($preserved)
foreach($name in @('startup-launch.txt','startup-result.json','startup-result.tmp')){if(Test-Path -LiteralPath "$clone/$name"){Move-Item -LiteralPath "$clone/$name" -Destination "$preserved/$name"}}
$backup="$clone/APF/.transaction-backup"
$snapshot=@(Get-ChildItem -LiteralPath $backup -Recurse -Force | ForEach-Object {
    $relative=$_.FullName.Substring($backup.Length+1).Replace('\','/')
    if($_.PSIsContainer){'D:'+$relative}else{$relative+':'+[ApfCore.ApfJson]::Sha256File($_.FullName)}
})
[Array]::Sort($snapshot,[StringComparer]::Ordinal)
[IO.File]::WriteAllText("$clone/expected-backup-snapshot.json",[ApfCore.ApfJson]::Serialize($snapshot))
$previous=[Environment]::GetEnvironmentVariable('APF_SETTINGS_ARGUMENTS','Process')
try{
    [Environment]::SetEnvironmentVariable('APF_SETTINGS_ARGUMENTS','--startup-stub','Process')
    Push-Location $workspace
    try{& "$workspace/Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe" "$clone/KSP_x64.exe" *> "$clone/mono-probe.log";$exit=$LASTEXITCODE}finally{Pop-Location}
}finally{[Environment]::SetEnvironmentVariable('APF_SETTINGS_ARGUMENTS',$previous,'Process')}
if($exit -ne 0 -or !(Test-Path -LiteralPath "$clone/startup-result.json") -or (Get-Content -LiteralPath "$clone/startup-result.json" -Raw) -cne 'passed'){Get-Content -LiteralPath "$clone/mono-probe.log" -Tail 12;throw "Actual Mono probe failed; private clone retained: $clone"}
$proof=Get-Content -LiteralPath "$clone/startup-proof.json" -Raw|ConvertFrom-Json
if($proof.runtime -cne 'KSP-Mono' -or $proof.settings -ne 8){throw 'Actual Mono/eight-setting proof missing.'}
[ordered]@{passed=$true;runtime=$proof.runtime;checks=$proof.checks;settings=$proof.settings;startupCalls=$proof.startupCalls;engineSha256=$expectedEngine;clone=$clone;unityGameplayTested=$false}|ConvertTo-Json -Compress
