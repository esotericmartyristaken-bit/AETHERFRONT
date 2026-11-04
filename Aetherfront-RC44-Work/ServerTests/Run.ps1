#requires -Version 7.4
[CmdletBinding()]
param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$apf = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$dotnet = Join-Path $apf 'tools/dotnet-sdk8/dotnet.exe'
$runtime = Join-Path $apf 'KESAS-VPS-Server/Server'
$mode = if ($Baseline) { 'baseline' } else { 'candidate' }
$assemblySource = if ($Baseline) { Join-Path $runtime 'AetherfrontContinuityServer.dll' } else { Join-Path $apf 'UpdateBuilds/MissionTime-20261104/code/AetherfrontContinuityServer.dll' }
$expected = if ($Baseline) { '98a5541f2701b7961039da3261f6c4bb74149d313f33dcafbeafb6950d824fa1' } else { 'ff3a6c99ef8a8a076ca77c0344788f7f8b59110cdb959b81537e2c2bc745dea9' }
if ((Get-FileHash -LiteralPath $assemblySource -Algorithm SHA256).Hash -ne $expected) { throw 'Unexpected continuity SHA256; do not silently test another candidate' }
$tag = $mode + '-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$run = Join-Path $apf ('UpdateBuilds/MissionTime-20261104/server-tests/' + $tag)
& $dotnet build (Join-Path $PSScriptRoot 'ServerTests.csproj') --configuration Release --nologo "-p:BuildTag=$tag" "-p:Continuity=$assemblySource"
if ($LASTEXITCODE -ne 0) { throw 'Mission-time integration adapter compilation failed' }
$exe = Join-Path $run 'bin/ServerTests.dll'
$evidence = Join-Path $run 'fixtures'
$output = @(& $dotnet $exe ('--' + $mode) $evidence 2>&1 | ForEach-Object ToString)
$testExit = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $run 'console.txt'), [string[]]$output)
$output
Write-Output "RESULTS $run"
if ($testExit -ne 0) { throw "Mission-time integration $mode failed: $testExit" }
