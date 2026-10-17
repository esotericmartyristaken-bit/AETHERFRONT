param([switch]$SkipBuild)
$ErrorActionPreference='Stop'
$dotnet=Join-Path $PSScriptRoot '../tools/dotnet-sdk8/dotnet.exe'
if(-not $SkipBuild){foreach($project in @('MonoHost/MonoHost.csproj','MonoAudit/MonoAudit.csproj','TransferTests/TransferTests.csproj','GitHubTests/GitHubTests.csproj')){& $dotnet build (Join-Path $PSScriptRoot $project) -c Release --nologo -v quiet;if($LASTEXITCODE -ne 0){throw "Build failed: $project"}}}
& $dotnet (Join-Path $PSScriptRoot 'MonoAudit/bin/Release/net8.0/MonoAudit.dll')
if($LASTEXITCODE -ne 0){throw 'KSP API audit failed'}
$fixture=Join-Path $PSScriptRoot 'MonoRegression-RC18'
$null=New-Item -ItemType Directory -Force -Path $fixture
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../Aetherfront-RC19-Work/TransferTests/bin/Release/net472') -File|Copy-Item -Destination $fixture -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Aetherfront-RC18-Work/Engine/bin/Release/net472/KesasInstall.dll') -Destination (Join-Path $fixture 'KesasInstall.dll') -Force
$hostPath=Join-Path $PSScriptRoot 'MonoHost/bin/Release/net472/MonoHost.exe'
$output=& $hostPath (Join-Path $fixture 'TransferTests.exe') 2>&1
if($LASTEXITCODE -ne 1 -or ($output|Out-String) -notmatch 'ServicePoint.set_ReceiveBufferSize'){throw 'Expected RC18 Mono failure was not reproduced'}
Write-Output 'PASS exact RC18 failure reproduced under KSP Mono'
& $hostPath (Join-Path $PSScriptRoot 'TransferTests/bin/Release/net472/TransferTests.exe')
if($LASTEXITCODE -ne 0){throw 'Corrected downloader failed KSP Mono regression suite'}
& $hostPath (Join-Path $PSScriptRoot 'GitHubTests/bin/Release/net472/GitHubTests.exe')
if($LASTEXITCODE -ne 0){throw 'GitHub transport failed KSP Mono regression suite'}
