#requires -Version 7.4
param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$BuildTag)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dot=Join-Path $workspace 'tools/dotnet-sdk8/dotnet.exe'
$root=Join-Path $workspace 'UpdateBuilds/RC48-20261105'
$client=Join-Path $workspace 'UpdateBuilds/RC46-20261104/mono-1'
$runtime=Join-Path $workspace 'KESAS-VPS-Server/Server'
$managed=Join-Path $workspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$cecil=Join-Path $workspace 'tools/ilspy/.store/ilspycmd/9.1.0.7988/ilspycmd/9.1.0.7988/tools/net8.0/any/Mono.Cecil.dll'
$code=Join-Path $root $BuildTag
if(Test-Path -LiteralPath $code){throw 'Use a fresh immutable code output'}
function Build([string]$Project,[string]$Name,[string[]]$Properties){
    & $dot build $Project -c Release --nologo -p:NuGetAudit=false "-p:RestoreSources=$workspace/tools/empty-nuget" "-p:BaseIntermediateOutputPath=$root/$Name-obj/" "-p:OutputPath=$root/$Name/" @Properties
    if($LASTEXITCODE -ne 0){throw "Build failed: $Name"}
}
Build "$PSScriptRoot/Patch/Patch.csproj" 'patch' @("-p:CecilPath=$cecil")
Build "$PSScriptRoot/../Aetherfront-RC43-Work/Client/AetherfrontPersonalClient.csproj" 'personal' @("-p:ClientPlugins=$client","-p:KspManaged=$managed")
Build "$PSScriptRoot/../Aetherfront-RC43-Work/Server/AetherfrontPersonalServer.csproj" 'server' @("-p:ServerReference=$runtime/Server.dll","-p:PersonalCore=$runtime/AetherfrontCore.dll","-p:ServerRuntime=$runtime")
$env:AETHERFRONT_KSP_MANAGED=$managed
foreach($row in @(
 @('personal',"$workspace/UpdateBuilds/RC47-20261105/code-4/AetherfrontPersonalClient.dll","$root/personal/AetherfrontPersonalClient.dll",'AetherfrontPersonalClient.dll'),
 @('client',"$workspace/UpdateBuilds/RC47-20261105/code-4/LmpClient.dll",'-','LmpClient.dll'),
 @('server',"$workspace/UpdateBuilds/RC47-20261105/code-4/AetherfrontPersonalServer.dll","$root/server/AetherfrontPersonalServer.dll",'AetherfrontPersonalServer.dll')
)){
 & $dot "$root/patch/Patch.dll" $row[0] $row[1] $row[2] "$code/$($row[3])"
 if($LASTEXITCODE -ne 0){throw "Pinned assembly graft failed: $($row[0])"}
}
foreach($name in @('Server.dll','AetherfrontContinuityServer.dll')){
 Copy-Item -LiteralPath "$workspace/UpdateBuilds/RC47-20261105/code-4/$name" -Destination "$code/$name"
}
Get-FileHash -LiteralPath @(Get-ChildItem -LiteralPath $code -Filter '*.dll' -File|ForEach-Object FullName)
