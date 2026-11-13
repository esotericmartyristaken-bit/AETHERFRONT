#requires -Version 7.4
[CmdletBinding()]param([string]$RunName=('ClientUpdate-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($RunName -cnotmatch '^ClientUpdate-[A-Za-z0-9-]{1,64}$'){throw 'Use a fresh ClientUpdate fixture name'}
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workspace='C:\Users\Administrator\Desktop\APF'
$run=Join-Path "$workspace/UpdateBuilds/RC56-Sync-Fixture" $RunName
if(Test-Path -LiteralPath $run){throw 'Preserve previous attempts; choose a new name'}
$archive="$workspace/KESAS-VPS-Server/Updates/kesas-rc60/APFClient-kesas-rc60.zip"
$feed="$workspace/KESAS-VPS-Server/Updates/kesas-rc60/APFClientUpdate.json"
$public="$workspace/KESAS-VPS-Server/secrets/update-signing-public.xml"
foreach($inputFile in @(@{path=$archive;sha='781d9703e83e719dbd01e41509f5049be0f3a828bec8aaec540c248b85f53a6e'},@{path=$feed;sha='8d095e34b68f32762506e8ed29b21cfe174eb0a7b3af7286117f6c2659804e33'},@{path=$public;sha='5d6a4b75d1af78a888c87b8c6203e00972fecbcfc6f604f351e68d77fc248ea1'})){
 if((Get-FileHash -LiteralPath $inputFile.path).Hash.ToLowerInvariant() -cne $inputFile.sha){throw 'Pinned RC60 input differs'}
}
$null=New-Item -ItemType Directory -Path "$run/Logs"
$dotnet="$workspace/tools/dotnet-sdk8/dotnet.exe";$ksp="$workspace/Aetherfront-KSP-Smoke";$managed="$ksp/KSP_x64_Data/Managed"
$cecil="$workspace/tools/dotnet-sdk6/sdk/6.0.428/Sdks/Microsoft.NET.ILLink.Tasks/tools/net6.0/Mono.Cecil.dll"
function Invoke-Check([string]$Name,[string]$Executable,[string[]]$Arguments){
 $PSNativeCommandUseErrorActionPreference=$false
 & $Executable @Arguments 2>&1 | Tee-Object -FilePath "$run/Logs/$Name.log"
 if($LASTEXITCODE -ne 0){throw "Candidate check failed: $Name; production untouched"}
}
function Build([string]$Name,[string]$Project,[string[]]$Properties=@()){
 Invoke-Check $Name $dotnet (@('build',"$source/$Project",'-c','Release','-o',"$run/$Name",'--nologo','-v:q','-p:UseSharedCompilation=false','-p:AutomaticallyUseReferenceAssemblyPackages=true')+$Properties)
}
function Source-Inventory {
 foreach($directory in @('Aetherfront-RC28-Work/Engine','Aetherfront-RC22-Work/Engine','Aetherfront-RC25-Work/ClientUi','Aetherfront-RC25-Work/ClientUi/Tests','Aetherfront-RC24-Work/NativeUi','Development/ClientUpdatePatch','Development/ClientUpdateTests','Development/ClientUpdateTests/NativeUi','Development/ClientUpdateTests/Mono')){
  Get-ChildItem -LiteralPath (Join-Path $source $directory) -File | Where-Object Extension -in @('.cs','.csproj','.ps1') | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}}
 }
}
$sources=@(Source-Inventory)
Expand-Archive -LiteralPath $archive -DestinationPath "$run/Baseline"
$plugins="$run/Baseline/GameData/LunaMultiplayer/Plugins"
Build 'Engine' 'Aetherfront-RC28-Work/Engine/Engine.csproj' @("-p:ApfCorePath=$plugins/ApfCore.dll","-p:ShippingManaged=$run/Baseline/KSP_x64_Data/Managed")
Build 'UiDonor' 'Aetherfront-RC25-Work/ClientUi/ClientUi.csproj' @("-p:BaselinePlugins=$plugins","-p:EngineAssembly=$run/Engine/KesasInstall.dll","-p:UnityManaged=$managed")
Build 'NativeUi' 'Aetherfront-RC24-Work/NativeUi/NativeUi.csproj' @("-p:EngineAssembly=$run/Engine/KesasInstall.dll")
Build 'Patch' 'Development/ClientUpdatePatch/Patch.csproj' @("-p:CecilPath=$cecil")
Invoke-Check 'Graft' $dotnet @("$run/Patch/Patch.dll",$plugins,"$run/UiDonor/AetherfrontConnectSource.dll","$run/Engine/KesasInstall.dll","$run/Patched",$managed)
Copy-Item -LiteralPath "$plugins/JsonFx.dll" -Destination "$run/Patched/JsonFx.dll"
Build 'FlowTests' 'Aetherfront-RC25-Work/ClientUi/Tests/FlowTests.csproj'
Invoke-Check 'AutomaticFlow' $dotnet @("$run/FlowTests/FlowTests.dll")
Build 'NativeUiTests' 'Development/ClientUpdateTests/NativeUi/NativeUiTests.csproj'
Invoke-Check 'NativeAutomaticRestart' "$run/NativeUiTests/NativeUiTests.exe" @()
Invoke-Check 'HiddenWindow' "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" @('-NoProfile','-STA','-File',"$source/Aetherfront-RC24-Work/NativeUi/Test-InstallWindow.ps1",'-CorePath',"$run/Patched/ApfCore.dll",'-EnginePath',"$run/Engine/KesasInstall.dll",'-UiPath',"$run/NativeUi/AetherfrontInstallUI.dll")
Build 'Tests' 'Development/ClientUpdateTests/Tests.csproj' @("-p:TestPlugins=$run/Patched","-p:TestEngine=$run/Engine/KesasInstall.dll")
$testArguments=@("$run/Baseline",$feed,$archive,$public)
Invoke-Check 'IncrementalAndRealArchive' "$run/Tests/ClientUpdateTests.exe" $testArguments
Build 'Mono' 'Development/ClientUpdateTests/Mono/MonoTests.csproj'
Build 'MonoHost' 'Aetherfront-RC56-Work/MonoHost/MonoHost.csproj'
Get-ChildItem -LiteralPath $plugins -Filter '*.dll' -File | Copy-Item -Destination "$run/Mono"
foreach($path in @("$run/Patched/LmpClient.dll","$run/Patched/ApfCore.dll","$run/Engine/KesasInstall.dll","$run/Tests/ClientUpdateTests.exe")){Copy-Item -LiteralPath $path -Destination "$run/Mono" -Force}
Copy-Item -LiteralPath "$run/Baseline/GameData/000_Harmony/0Harmony.dll" -Destination "$run/Mono/0Harmony.dll"
foreach($name in @('System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Numerics.dll')){Copy-Item -LiteralPath "$run/Baseline/KSP_x64_Data/Managed/$name" -Destination "$run/Mono/$name"}
$priorGame=$env:RC56_KSP_REFERENCE;$priorMono=$env:RC56_MONO_ROOT;$priorArguments=$env:CLIENT_UPDATE_TEST_ARGUMENTS
try{$env:RC56_KSP_REFERENCE=$ksp;$env:RC56_MONO_ROOT="$run/Mono";$env:CLIENT_UPDATE_TEST_ARGUMENTS=$testArguments -join '|';Invoke-Check 'ActualKspMono' "$run/MonoHost/MonoHost.exe" @("$run/Mono/MonoTests.exe")}
finally{$env:RC56_KSP_REFERENCE=$priorGame;$env:RC56_MONO_ROOT=$priorMono;$env:CLIENT_UPDATE_TEST_ARGUMENTS=$priorArguments}
if(($sources|ConvertTo-Json -Compress) -cne (@(Source-Inventory)|ConvertTo-Json -Compress)){throw 'Sources changed during validation'}
$overlay="$run/ClientOverlay/GameData/LunaMultiplayer";$null=New-Item -ItemType Directory -Path "$overlay/Plugins","$overlay/Launcher"
foreach($name in @('LmpClient.dll','ApfCore.dll')){Copy-Item -LiteralPath "$run/Patched/$name" -Destination "$overlay/Plugins/$name"}
Copy-Item -LiteralPath "$run/Engine/KesasInstall.dll" -Destination "$overlay/Plugins/KesasInstall.dll"
Copy-Item -LiteralPath "$run/NativeUi/AetherfrontInstallUI.dll" -Destination "$overlay/Launcher/AetherfrontInstallUI.dll"
$result=@{atUtc=[DateTime]::UtcNow.ToString('o');baseline='kesas-rc60';validated=$true;deployed=$false;signed=$false;versionBumpRequired=$true;sourceFiles=$sources;files=@(Get-ChildItem -LiteralPath "$run/ClientOverlay" -Recurse -File|ForEach-Object{@{path=[IO.Path]::GetRelativePath("$run/ClientOverlay",$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant();bytes=$_.Length}})}
[IO.File]::WriteAllText("$run/Candidate.json",($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Output "Validated next-update overlay: $run/Candidate.json. NOT signed, published or deployed. Current RC60 unchanged."
