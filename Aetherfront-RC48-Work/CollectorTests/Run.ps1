#requires -Version 7.4
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$SourceDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$stage=[IO.Path]::GetFullPath($OutputDirectory)
if((-not $stage.StartsWith((Join-Path $workspace 'UpdateBuilds/RC48-'),[StringComparison]::OrdinalIgnoreCase) -and -not $stage.StartsWith((Join-Path $workspace 'UpdateBuilds/RC49-'),[StringComparison]::OrdinalIgnoreCase)) -or (Test-Path -LiteralPath $stage)){throw 'Use a new private RC48/RC49 test directory'}
$null=New-Item -ItemType Directory -Path $stage
$managed=Join-Path $workspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$plugins=Join-Path $workspace 'UpdateBuilds/RC46-20261104/mono-1'
foreach($name in @('AetherfrontCore.dll','JsonFx.dll')){Copy-Item -LiteralPath "$plugins/$name" -Destination $stage}
Copy-Item -LiteralPath "$workspace/Aetherfront-KSP-Smoke/GameData/000_Harmony/0Harmony.dll" -Destination $stage
$references=@('mscorlib.dll','System.dll','System.Core.dll')|ForEach-Object {'-r:'+(Join-Path $managed $_)}
$references+=@('AetherfrontCore.dll','JsonFx.dll','0Harmony.dll')|ForEach-Object {'-r:'+(Join-Path $stage $_)}
$source=Join-Path $PSScriptRoot '../../Aetherfront-RC43-Work'
$reviewedSource=if($SourceDirectory){[IO.Path]::GetFullPath($SourceDirectory)}else{$source}
$sources=@("$PSScriptRoot/Program.cs","$PSScriptRoot/Stubs.cs","$source/Mono/MonoGate.cs")
$sources+=@('Client/ScienceEvents.cs','Client/SubjectHistory.cs','Client/ScienceOutbox.cs','Client/ScienceClaim.cs','Client/ScienceRetryPolicy.cs','Shared/ScienceReceipt.cs')|ForEach-Object {if(Test-Path -LiteralPath (Join-Path $reviewedSource $_)){Join-Path $reviewedSource $_}else{Join-Path $source $_}}
if(Test-Path -LiteralPath "$reviewedSource/Client/RecoverySubjects.cs"){$sources+="$reviewedSource/Client/RecoverySubjects.cs"}
& "$workspace/tools/dotnet-sdk8/dotnet.exe" "$workspace/tools/dotnet-sdk8/sdk/8.0.424/Roslyn/bincore/csc.dll" -nologo -noconfig -nostdlib -langversion:latest -target:exe -platform:x64 -optimize+ "-out:$stage/collector.exe" @references @sources
if($LASTEXITCODE -ne 0){throw 'Collector fixture compilation failed'}
$env:AF_SCIENCE_MONO_ROOT=$stage
Push-Location $workspace
try{
 & "$workspace/Aetherfront-RC20-Work/MonoHost/bin/Release/net472/MonoHost.exe" "$stage/collector.exe" 2>&1|Tee-Object -FilePath "$stage/console.txt"
 if($LASTEXITCODE -ne 0){throw 'Collector fixtures failed'}
}finally{Pop-Location}
