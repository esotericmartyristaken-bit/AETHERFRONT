#requires -Version 7.4
[CmdletBinding()]
param([switch]$OutboxOnly, [switch]$ReceiverOnly, [string]$OutputDirectory, [string]$CandidateDirectory, [string]$ClientPlugins)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($OutboxOnly -and $ReceiverOnly) { throw 'Choose only one gate selector' }
$apf = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$artifactParent = Join-Path $apf 'UpdateBuilds/ScienceTransmit-20261104'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $artifactParent 'outbox-mono' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($artifactParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and -not $OutputDirectory.StartsWith((Join-Path $apf 'UpdateBuilds/RC47-20261105') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and -not $OutputDirectory.StartsWith((Join-Path $apf 'UpdateBuilds/RC48-20261105') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must remain under the reviewed science evidence roots' }
$run = Join-Path $OutputDirectory ((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
$dotnet = Join-Path $apf 'tools/dotnet-sdk8/dotnet.exe'
$compiler = Join-Path $apf 'tools/dotnet-sdk8/sdk/8.0.424/Roslyn/bincore/csc.dll'
$managed = Join-Path $apf 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$hostExecutable = Join-Path $apf 'Aetherfront-RC20-Work/MonoHost/bin/Release/net472/MonoHost.exe'
$plugins = Join-Path $artifactParent 'client'
$candidate = Join-Path $artifactParent 'code'
if($ClientPlugins){$plugins=[IO.Path]::GetFullPath($ClientPlugins)}
if($CandidateDirectory){$candidate=[IO.Path]::GetFullPath($CandidateDirectory)}
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inputs = @($PSCommandPath, $hostExecutable, $compiler, (Join-Path $apf 'Aetherfront-KSP-Smoke/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'))
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll') | ForEach-Object { $p = Join-Path $managed $_; $inputs += $p; '-r:' + $p }
$savedRoot = $env:AF_SCIENCE_MONO_ROOT
$savedPhase = $env:AF_SCIENCE_MONO_PHASE
$allSucceeded = $true
Push-Location $apf
try {
    $env:AF_SCIENCE_MONO_ROOT = $run
    foreach ($phase in @('outbox', 'receiver')) {
        if (($phase -eq 'outbox' -and $ReceiverOnly) -or ($phase -eq 'receiver' -and $OutboxOnly)) { continue }
        $stage = Join-Path $run $phase
        $null = New-Item -ItemType Directory -Path $stage
        foreach ($name in @('AetherfrontCore.dll', 'JsonFx.dll')) {
            $assemblySource = Join-Path $plugins $name
            Copy-Item -LiteralPath $assemblySource -Destination (Join-Path $stage $name)
            $inputs += $assemblySource
        }
        $sources = @((Join-Path $PSScriptRoot 'MonoGate.cs'))
        $compileReferences = @($references)
        foreach ($name in @('AetherfrontCore.dll', 'JsonFx.dll')) { $compileReferences += '-r:' + (Join-Path $stage $name) }
        if ($phase -eq 'outbox') {
            $sources += Join-Path $PSScriptRoot 'OutboxProgram.cs'
            $sources += Join-Path $PSScriptRoot 'OutboxStubs.cs'
            $sources += @('Client/ScienceOutbox.cs', 'Client/ScienceClaim.cs', 'Client/ScienceRetryPolicy.cs', 'Shared/ScienceReceipt.cs') | ForEach-Object { Join-Path $sourceRoot $_ }
        } else {
            $expected = @{
                'LmpClient.dll' = '6eb9ecd45e45fa908ca532eea1d632ddefae7a24547d9bd1d976390fcd86f801'
                'AetherfrontPersonalClient.dll' = '02157055d47a3fe3c68b05fe8637f666db5af1815b8d5d23e14f2b78eb36da61'
            }
            if($CandidateDirectory){
                foreach($name in $expected.Keys|ForEach-Object {$_}){
                    $audit=Get-Content -LiteralPath "$candidate/$name.audit.json" -Raw|ConvertFrom-Json
                    if($audit.version -cnotin @('kesas-rc47','kesas-rc48') -or -not $audit.identityPreserved -or -not $audit.allOtherMethodsUnchanged){throw 'Unreviewed receiver candidate'}
                    $expected[$name]=$audit.outputSha256
                }
            }
            foreach ($name in $expected.Keys) {
                $assemblySource = Join-Path $candidate $name
                if ((Get-FileHash -LiteralPath $assemblySource -Algorithm SHA256).Hash -ne $expected[$name]) { throw "Unexpected final candidate SHA256: $name" }
                Copy-Item -LiteralPath $assemblySource -Destination (Join-Path $stage $name)
                $inputs += $assemblySource
            }
            foreach ($dependency in Get-ChildItem -LiteralPath $plugins -Filter '*.dll') {
                if (-not (Test-Path -LiteralPath (Join-Path $stage $dependency.Name))) {
                    Copy-Item -LiteralPath $dependency.FullName -Destination (Join-Path $stage $dependency.Name)
                    $inputs += $dependency.FullName
                }
            }
            foreach ($name in @('LmpClient.dll', 'Lidgren.Network.dll')) { $compileReferences += '-r:' + (Join-Path $stage $name) }
            $sources += Join-Path $PSScriptRoot 'ReceiverProgram.cs'
        }
        $inputs += $sources
        $exe = Join-Path $stage ($phase + '-mono.exe')
        & $dotnet $compiler -nologo -noconfig -nostdlib -langversion:latest -target:exe -platform:x64 -optimize+ "-out:$exe" @compileReferences @sources
        if ($LASTEXITCODE -ne 0) { throw "$phase Mono fixture compilation failed" }
        $env:AF_SCIENCE_MONO_PHASE = $phase
        if($phase -eq 'receiver'){
            $env:AF_SCIENCE_MONO_CLIENT_HASH=$expected['LmpClient.dll']
            $env:AF_SCIENCE_MONO_HELPER_HASH=$expected['AetherfrontPersonalClient.dll']
        }
        $rawOutput = @(& $hostExecutable $exe 2>&1 | ForEach-Object ToString)
        $phaseExit = $LASTEXITCODE
        [IO.File]::WriteAllLines((Join-Path $run ($phase + '-console.txt')), [string[]]$rawOutput)
        $output = @($rawOutput | ForEach-Object { $_.TrimStart([char]0xfeff) })
        [IO.File]::WriteAllLines((Join-Path $run ($phase + '-load-warnings.txt')), [string[]]@($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -notmatch '^(PASS |FAIL |RESULT |MONO |FIXTURES )' }))
        $output
        if ($phaseExit -ne 0) { $allSucceeded = $false }
    }
} finally {
    Pop-Location
    $env:AF_SCIENCE_MONO_ROOT = $savedRoot
    $env:AF_SCIENCE_MONO_PHASE = $savedPhase
    $hashes = @($inputs | Select-Object -Unique | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | ForEach-Object { $h = Get-FileHash -LiteralPath $_ -Algorithm SHA256; [pscustomobject]@{ path = $h.Path; sha256 = $h.Hash.ToLowerInvariant() } })
    [IO.File]::WriteAllText((Join-Path $run 'inputs.json'), (ConvertTo-Json -Depth 5 -InputObject $hashes))
    Write-Output "FIXTURES $run"
}
if (-not $allSucceeded) { throw 'Mono gate failed; inspect phase results and separate load-warnings files' }
