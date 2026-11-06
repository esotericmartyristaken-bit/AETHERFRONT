#requires -Version 7.4
param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$BuildTag)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=Join-Path $workspace 'UpdateBuilds/RC49-Recovery-20261106'
$code=Join-Path $root $BuildTag
if(Test-Path -LiteralPath $code){throw 'Use a fresh immutable candidate directory'}
$dot=Join-Path $workspace 'tools/dotnet-sdk8/dotnet.exe'
$cecil=Join-Path $workspace 'tools/ilspy/.store/ilspycmd/9.1.0.7988/ilspycmd/9.1.0.7988/tools/net8.0/any/Mono.Cecil.dll'
$managed=Join-Path $workspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$client=Join-Path $workspace 'UpdateBuilds/RC46-20261104/mono-1'
$runtime=Join-Path $workspace 'KESAS-VPS-Server/Server'
function Build([string]$Project,[string]$Name,[string[]]$Properties){
 & $dot build $Project -c Release --nologo -p:NuGetAudit=false "-p:RestoreSources=$workspace/tools/empty-nuget" "-p:BaseIntermediateOutputPath=$root/$BuildTag-$Name-obj/" "-p:OutputPath=$root/$BuildTag-$Name/" @Properties
 if($LASTEXITCODE -ne 0){throw "Build failed: $Name"}
}
Build "$PSScriptRoot/../Aetherfront-RC48-Work/Patch/Patch.csproj" 'patch' @("-p:CecilPath=$cecil")
Build "$PSScriptRoot/../Aetherfront-RC43-Work/Client/AetherfrontPersonalClient.csproj" 'personal' @("-p:ClientPlugins=$client","-p:KspManaged=$managed")
Build "$PSScriptRoot/../Aetherfront-RC43-Work/Server/AetherfrontPersonalServer.csproj" 'server' @("-p:ServerReference=$runtime/Server.dll","-p:PersonalCore=$runtime/AetherfrontCore.dll","-p:ServerRuntime=$runtime")
Build "$PSScriptRoot/ArtifactTests/ArtifactTests.csproj" 'artifact-tests' @("-p:CecilPath=$cecil")
$env:AETHERFRONT_KSP_MANAGED=$managed
$null=New-Item -ItemType Directory -Path $code
foreach($row in @(@('personal','AetherfrontPersonalClient.dll'),@('client','LmpClient.dll'),@('server','AetherfrontPersonalServer.dll'))){
 $donor=if($row[0]-eq 'client'){'-'}else{"$root/$BuildTag-$($row[0])/$($row[1])"}
 & $dot "$root/$BuildTag-patch/Patch.dll" $row[0] "$workspace/UpdateBuilds/RC48-20261105/code-5/$($row[1])" $donor "$code/$($row[1])" 'kesas-rc49'
 if($LASTEXITCODE -ne 0){throw "Candidate graft failed: $($row[0])"}
}
foreach($name in @('Server.dll','AetherfrontContinuityServer.dll')){Copy-Item -LiteralPath "$workspace/UpdateBuilds/RC48-20261105/code-5/$name" -Destination "$code/$name"}
Copy-Item -LiteralPath "$runtime/AetherfrontCore.dll" -Destination "$code/AetherfrontCore.dll"
$admission=& $dot "$root/$BuildTag-artifact-tests/ArtifactTests.dll" "$code/AetherfrontPersonalClient.dll" $managed
if($LASTEXITCODE -ne 0){throw 'Exact candidate artifact cannot boot science collector'}
[IO.File]::WriteAllText("$code/AetherfrontPersonalClient.dll.startup.json",($admission -join "`n"),[Text.UTF8Encoding]::new($false))
$admission
Get-FileHash -LiteralPath @(Get-ChildItem -LiteralPath $code -Filter '*.dll' -File|ForEach-Object FullName)
