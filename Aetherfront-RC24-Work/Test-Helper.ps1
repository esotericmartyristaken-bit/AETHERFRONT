#requires -Version 5.1
$ErrorActionPreference='Stop'
$work=$PSScriptRoot
foreach($path in @('../Aetherfront-RC23-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins/JsonFx.dll','Patched/ApfCore.dll','Engine/bin/Release/net472/KesasInstall.dll')){[void][Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath((Join-Path $work $path)))}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixture=[IO.Path]::GetFullPath((Join-Path $work 'NativeFixture-454c4efb6d0e4bc5b4a2c66bd6d26646'))
if(!$fixture.StartsWith([IO.Path]::GetFullPath($work)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture outside owned work directory.'}
$proof=Get-Content -LiteralPath (Join-Path $fixture 'result.json') -Raw|ConvertFrom-Json
if(!$proof.passed -or $proof.checks -ne 14){throw 'Expected the completed original native fixture.'}
$helper=Join-Path $fixture 'GameData/LunaMultiplayer'
$null=New-Item -ItemType Directory -Path (Join-Path $helper 'Plugins'),(Join-Path $helper 'Launcher') -Force
foreach($name in @('ApfCore.dll','KesasInstall.dll','JsonFx.dll')){
    $source=@{'ApfCore.dll'="$work/Patched/ApfCore.dll";'KesasInstall.dll'="$work/Engine/bin/Release/net472/KesasInstall.dll";'JsonFx.dll'="$work/../Aetherfront-RC23-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins/JsonFx.dll"}[$name]
    Copy-Item -LiteralPath $source -Destination (Join-Path $helper "Plugins/$name") -Force
}
Copy-Item -LiteralPath "$work/NativeUi/bin/Release/net472/AetherfrontInstallUI.dll" -Destination (Join-Path $helper 'Launcher/AetherfrontInstallUI.bin') -Force
Copy-Item -LiteralPath "$work/APFApplyUpdate.ps1" -Destination (Join-Path $helper 'APFApplyUpdate.ps1') -Force
$stage=Join-Path $fixture ('APF/.staging/script-helper-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $stage
$archive=Join-Path $stage 'fixture.zip'
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{$entry=$zip.CreateEntry('TestCore/example.cfg');$writer=[IO.StreamWriter]::new($entry.Open());try{$writer.Write('native PowerShell helper content')}finally{$writer.Dispose()}}finally{$zip.Dispose()}
$package=[ApfCore.ApfPackage]::new()
$package.Id='testcore';$package.Version='script-test';$package.ArchiveUrl='https://217.216.49.250:18866/fixture.zip';$package.Sha256=[ApfCore.ApfJson]::Sha256File($archive);$package.SizeBytes=([IO.FileInfo]$archive).Length;$package.Destination='GameData';$package.StripPrefix='TestCore'
$manifest=[ApfCore.ApfPackManifest]::new()
$manifest.ServerId='kesas-b6286df413b9426f';$manifest.PackId='kesas';$manifest.PackVersion='kesas-rc24';$manifest.RequiredClientVersion='kesas-rc24';$manifest.KspVersion='1.12.5'
foreach($name in @('Squad','SquadExpansion','LunaMultiplayer','000_Harmony')){$manifest.ProtectedRoots.Add($name)}
$manifest.ManagedPackages.Add($package)
$transaction=[ApfCore.ApfSyncTransaction]::new()
$transaction.GameRoot=$fixture;$transaction.GameDataPath=Join-Path $fixture 'GameData';$transaction.KspExecutable=Join-Path $fixture 'KSP_x64.exe';$transaction.ProcessId=0;$transaction.KspArguments.Add('--native-script-'+[Guid]::NewGuid().ToString('N'));$transaction.StatePath=Join-Path $helper 'Data/APFPackState.json';$transaction.Manifest=$manifest
$staged=[ApfCore.ApfStagedPackage]::new();$staged.Package=$package;$staged.ArchivePath=$archive;$transaction.Packages.Add($staged)
$key=[ApfCore.ApfTransactionSecurity]::LoadKey((Join-Path $helper 'Data/APFHelper.key'))
[ApfCore.ApfTransactionSecurity]::Sign($transaction,$key)
$request=Join-Path $stage 'transaction.json'
[IO.File]::WriteAllText($request,[ApfCore.ApfJson]::Serialize($transaction))
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $helper 'APFApplyUpdate.ps1') -UpdateKind pack -TransactionPath $request -Headless
if($LASTEXITCODE){throw 'Native helper process failed.'}
if([IO.File]::ReadAllText((Join-Path $fixture 'GameData/TestCore/example.cfg')) -cne 'native PowerShell helper content'){throw 'Helper content mismatch.'}
$last=Get-Content -LiteralPath (Join-Path $fixture 'APF/LastInstallResult.json') -Raw|ConvertFrom-Json
if($last.status -cne 'complete'){throw 'Missing final helper success marker.'}
$process=[Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{FileName=(Join-Path $fixture 'KSP_x64.exe');UseShellExecute=$false})
try{if(!$process.WaitForExit(10000)-or$process.ExitCode){throw 'Headless helper retained launch locks.'}}finally{$process.Dispose()}
Write-Output 'PASS native PowerShell shadow loader, .bin UI payload, authenticated pack, completion marker and released locks.'
