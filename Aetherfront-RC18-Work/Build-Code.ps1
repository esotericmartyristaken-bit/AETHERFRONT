$ErrorActionPreference='Stop'
$dotnet=Join-Path $PSScriptRoot '../tools/dotnet-sdk8/dotnet.exe'
foreach($project in @('Engine/Engine.csproj','ClientUi/ClientUi.csproj','Patcher/Patcher.csproj','GameStub/GameStub.csproj')) {
    & $dotnet build (Join-Path $PSScriptRoot $project) -c Release --nologo -v quiet
    if($LASTEXITCODE -ne 0){throw "Build failed: $project"}
}
& $dotnet (Join-Path $PSScriptRoot 'Patcher/bin/Release/net8.0/Patcher.dll') (Join-Path $PSScriptRoot '../KESAS-RC17-Work/Patched/LmpClient.dll') (Join-Path $PSScriptRoot '../KESAS-RC17-Work/Patched/ApfCore.dll') (Join-Path $PSScriptRoot 'ClientUi/bin/Release/net472/KesasConnectSource.dll') (Join-Path $PSScriptRoot 'Patched')
if($LASTEXITCODE -ne 0){throw 'Binary graft validation failed.'}
& $dotnet build (Join-Path $PSScriptRoot 'Tests/Tests.csproj') -c Release --nologo -v quiet
if($LASTEXITCODE -ne 0){throw 'Test build failed.'}
