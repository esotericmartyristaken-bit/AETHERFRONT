#requires -Version 5.1
$ErrorActionPreference='Stop'
$work=[IO.Path]::GetFullPath($PSScriptRoot)
foreach($path in @('../Aetherfront-RC24-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins/JsonFx.dll','Patched/ApfCore.dll','Engine/bin/Release/net472/KesasInstall.dll')){[void][Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath((Join-Path $work $path)))}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixture=Join-Path $work ('HelperFixture-'+[Guid]::NewGuid().ToString('N'))
if(!$fixture.StartsWith($work+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid owned helper fixture.'}
$helper=Join-Path $fixture 'GameData/LunaMultiplayer'
foreach($relative in @('GameData/Squad','GameData/LunaMultiplayer/Plugins','GameData/LunaMultiplayer/Data','GameData/TestCore','APF/.staging/helper')){[void][IO.Directory]::CreateDirectory((Join-Path $fixture $relative))}
[IO.File]::Copy((Join-Path $work '../Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe'),(Join-Path $fixture 'KSP_x64.exe'))
[IO.File]::WriteAllText((Join-Path $helper 'Data/APFServer.txt'),'217.216.49.250:18865')
[IO.File]::WriteAllText((Join-Path $helper 'Data/APFTlsCertificatePins.txt'),'34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09')
[IO.File]::WriteAllText((Join-Path $fixture 'settings.cfg'),"KEY_BINDING = retained`n")
[IO.File]::WriteAllText((Join-Path $fixture 'GameData/TestCore/example.cfg'),'old helper content')
foreach($name in @('ApfCore.dll','KesasInstall.dll','JsonFx.dll')){
    $source=@{'ApfCore.dll'="$work/Patched/ApfCore.dll";'KesasInstall.dll'="$work/Engine/bin/Release/net472/KesasInstall.dll";'JsonFx.dll'="$work/../Aetherfront-RC24-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins/JsonFx.dll"}[$name]
    [IO.File]::Copy($source,(Join-Path $helper "Plugins/$name"))
}
[IO.File]::Copy((Join-Path $work 'APFApplyUpdate.ps1'),(Join-Path $helper 'APFApplyUpdate.ps1'))
$stage=Join-Path $fixture 'APF/.staging/helper'
$archive=Join-Path $stage 'fixture.zip'
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{$writer=[IO.StreamWriter]::new($zip.CreateEntry('TestCore/example.cfg').Open());try{$writer.Write('quiet prepared helper content')}finally{$writer.Dispose()}}finally{$zip.Dispose()}
$package=[ApfCore.ApfPackage]::new()
$package.Id='testcore';$package.Version='script-test';$package.ArchiveUrl='https://217.216.49.250:18866/fixture.zip';$package.Sha256=[ApfCore.ApfJson]::Sha256File($archive);$package.SizeBytes=([IO.FileInfo]$archive).Length;$package.Destination='GameData';$package.StripPrefix='TestCore'
$manifest=[ApfCore.ApfPackManifest]::new()
$manifest.ServerId='kesas-b6286df413b9426f';$manifest.PackId='kesas';$manifest.PackVersion='kesas-rc25';$manifest.RequiredClientVersion='kesas-rc25';$manifest.KspVersion='1.12.5'
foreach($name in @('Squad','SquadExpansion','LunaMultiplayer','000_Harmony')){$manifest.ProtectedRoots.Add($name)}
$manifest.ManagedPackages.Add($package)
$transaction=[ApfCore.ApfSyncTransaction]::new()
$transaction.GameRoot=$fixture;$transaction.GameDataPath=Join-Path $fixture 'GameData';$transaction.KspExecutable=Join-Path $fixture 'KSP_x64.exe';$transaction.ProcessId=0;$transaction.StatePath=Join-Path $helper 'Data/APFPackState.json';$transaction.Manifest=$manifest
$staged=[ApfCore.ApfStagedPackage]::new();$staged.Package=$package;$staged.ArchivePath=$archive;$transaction.Packages.Add($staged)
$key=[ApfCore.ApfTransactionSecurity]::LoadOrCreateKey((Join-Path $helper 'Data/APFHelper.key'))
[ApfCore.ApfTransactionSecurity]::Sign($transaction,$key)
$request=Join-Path $stage 'transaction.json'
[IO.File]::WriteAllText($request,[ApfCore.ApfJson]::Serialize($transaction))
[KesasInstall.PreparedPack]::Prepare($transaction,$request,[Action[string]]{param($message)},[Func[bool]]{return $false})
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $helper 'APFApplyUpdate.ps1') -UpdateKind pack -TransactionPath $request -Headless
if($LASTEXITCODE){throw 'Hidden helper process failed.'}
if([IO.File]::ReadAllText((Join-Path $fixture 'GameData/TestCore/example.cfg')) -cne 'quiet prepared helper content'){throw 'Helper content mismatch.'}
$last=Get-Content -LiteralPath (Join-Path $fixture 'APF/LastInstallResult.json') -Raw|ConvertFrom-Json
if($last.status -cne 'complete' -or !(Test-Path -LiteralPath ($request+'.ready')) -or !(Test-Path -LiteralPath ($request+'.consumed'))){throw 'Missing helper completion/readiness/consumption proof.'}
if([IO.Directory]::Exists((Join-Path $helper 'Launcher'))){throw 'Test must not supply any desktop UI payload.'}
$process=[Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{FileName=(Join-Path $fixture 'KSP_x64.exe');UseShellExecute=$false})
try{if(!$process.WaitForExit(10000)-or$process.ExitCode){throw 'Helper retained executable locks.'}}finally{$process.Dispose()}
[IO.File]::WriteAllText((Join-Path $fixture 'result.json'),('{"passed":true,"productionPowerShellHelper":true,"preparedPack":true,"desktopUiSupplied":false,"headlessOnly":true,"unityGameplayTested":false}'))
Write-Output "PASS production quiet PowerShell helper, shadow .bin dependencies, prepared authenticated pack, completion marker and released locks. Fixture=$fixture"
