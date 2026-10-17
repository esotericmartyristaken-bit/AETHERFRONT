$ErrorActionPreference='Stop'
$dotnet=Join-Path $PSScriptRoot '../tools/dotnet-sdk8/dotnet.exe'
foreach($project in @('Engine/Engine.csproj','Patcher/Patcher.csproj','GameStub/GameStub.csproj')) {
    & $dotnet build (Join-Path $PSScriptRoot $project) -c Release --nologo -v quiet
    if($LASTEXITCODE -ne 0){throw "Build failed: $project"}
}
& $dotnet (Join-Path $PSScriptRoot 'Patcher/bin/Release/net8.0/Patcher.dll')
if($LASTEXITCODE -ne 0){throw 'Client version audit failed'}
foreach($project in @('Tests/Tests.csproj','TransferTests/TransferTests.csproj','BinaryChecks/BinaryChecks.csproj','UpdaterFixture/UpdaterFixture.csproj','PublicTransferProbe/PublicTransferProbe.csproj','UpgradeDownloadProbe/UpgradeDownloadProbe.csproj')) {
    & $dotnet build (Join-Path $PSScriptRoot $project) -c Release --nologo -v quiet
    if($LASTEXITCODE -ne 0){throw "Build failed: $project"}
}
& (Join-Path $PSScriptRoot 'Test-KspRuntime.ps1')
if($LASTEXITCODE -ne 0){throw 'KSP Mono compatibility gate failed'}
