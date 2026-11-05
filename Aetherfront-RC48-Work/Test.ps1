#requires -Version 7.4
param([Parameter(Mandatory)][string]$Code,[Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$TestTag)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=Join-Path $workspace "UpdateBuilds/RC48-20261105/$TestTag"
if(Test-Path -LiteralPath $root){throw 'Use a new evidence directory'}
$null=New-Item -ItemType Directory -Path $root
$candidate=[IO.Path]::GetFullPath($Code)
$runtime=Join-Path $workspace 'KESAS-VPS-Server/Server'
$client=Join-Path $workspace 'UpdateBuilds/RC46-20261104/mono-1'
$dot=Join-Path $workspace 'tools/dotnet-sdk8/dotnet.exe'
function TestBuild([string]$Project,[string]$Name,[string[]]$Properties){
 & $dot build $Project -c Release --nologo -p:NuGetAudit=false "-p:RestoreSources=$workspace/tools/empty-nuget" "-p:BaseIntermediateOutputPath=$root/$Name-obj/" "-p:OutputPath=$root/$Name/" @Properties
 if($LASTEXITCODE -ne 0){throw "Test build failed: $Name"}
}
function Run([string]$Name,[string]$Dll,[string[]]$Arguments){
 & $dot $Dll @Arguments 2>&1|Tee-Object -FilePath "$root/$Name.txt"
 if($LASTEXITCODE -ne 0){throw "Test failed: $Name"}
}
TestBuild "$PSScriptRoot/../Aetherfront-RC46-Work/Tests/Tests.csproj" 'research' @("-p:ClientPlugins=$client")
Run 'research' "$root/research/Tests.dll" @()
TestBuild "$PSScriptRoot/../Aetherfront-RC43-Work/ClientTests/ClientTests.csproj" 'outbox' @("-p:ClientPlugins=$client","-p:TestArtifactsRoot=$root/outbox-evidence")
# Legacy JsonFx emits Mono-specific IL for nonempty generic lists. Preserve
# the .NET 8 failures as evidence, then require the same paths under KSP Mono.
& $dot "$root/outbox/ClientTests.dll" 2>&1|Tee-Object -FilePath "$root/outbox.txt"
$outboxExit=$LASTEXITCODE
$resultFile=Get-ChildItem -LiteralPath "$root/outbox-evidence/runs" -Recurse -Filter 'results.json' -File|Select-Object -First 1
$outboxResult=Get-Content -LiteralPath $resultFile.FullName -Raw|ConvertFrom-Json
$monoOnly=@('JsonFx-nonempty-list-runtime-compatibility','old-v1-migration-preserves-evidence','actual-Core-decimal-six-place-roundtrip','attempt-save-failure-after-initial-persist','receipt-retirement-save-failure-restart-recovers','award-chunk-progression-and-restart','held-same-release-remains-held','held-new-release-resumes-evidence','load-future-retry-clamped-to-60-seconds')
$failures=@($outboxResult.results|Where-Object {-not $_.passed}|ForEach-Object name)
if($outboxExit -ne 1 -or $failures.Count -ne $monoOnly.Count -or @($failures|Where-Object {$_ -notin $monoOnly}).Count){throw 'Unexpected .NET 8 outbox failure; Mono cannot waive it'}
& "$PSScriptRoot/../Aetherfront-RC43-Work/Mono/Run.ps1" -CandidateDirectory $candidate -ClientPlugins $client -OutputDirectory "$root/science-mono"
TestBuild "$PSScriptRoot/../Aetherfront-RC47-Work/Tests/Tests.csproj" 'native-store' @("-p:Candidate=$candidate","-p:Runtime=$runtime")
Run 'native-store' "$root/native-store/Tests.dll" @("$root/native-store-fixtures",$runtime)
& "$PSScriptRoot/../Aetherfront-RC46-Work/Tests/Run-Mono.ps1" -Candidate "$candidate/AetherfrontPersonalClient.dll" -ClientPlugins $client -OutputDirectory "$root/research-mono"
if($LASTEXITCODE -ne 0){throw 'Native Mono research tests failed'}
TestBuild "$PSScriptRoot/../Aetherfront-RC43-Work/ServerTests/ServerTests.csproj" 'science-server' @("-p:Candidate=$candidate","-p:Runtime=$runtime","-p:EvidenceRoot=$root/science-server-evidence")
$null=New-Item -ItemType Directory -Path "$root/science-server-evidence"
# Execute the tested Store with the actual science handler in the same process.
Copy-Item -LiteralPath "$candidate/Server.dll" -Destination "$root/science-server/Server.dll" -Force
Run 'science-server' "$root/science-server/ServerTests.dll" @()
Get-FileHash -LiteralPath @(Get-ChildItem -LiteralPath $candidate -Filter '*.dll' -File|ForEach-Object FullName)
