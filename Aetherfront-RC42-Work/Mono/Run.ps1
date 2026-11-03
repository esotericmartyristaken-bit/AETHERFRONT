#requires -Version 7.4
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$apf=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $apf 'UpdateBuilds/RC42-20261103/mono'}
$dotnet=Join-Path $apf 'tools/dotnet-sdk8/dotnet.exe'
$compiler=Join-Path $apf 'tools/dotnet-sdk8/sdk/8.0.424/Roslyn/bincore/csc.dll'
$managed=Join-Path $apf 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$hostExecutable=Join-Path $apf 'Aetherfront-RC20-Work/MonoHost/bin/Release/net472/MonoHost.exe'
$null=New-Item -ItemType Directory -Path $OutputDirectory -Force
$testExecutable=Join-Path $OutputDirectory 'StorageMonoTests.exe'
& $dotnet $compiler -nologo -noconfig -nostdlib -target:exe -platform:x64 -optimize+ "-out:$testExecutable" "-r:$managed/mscorlib.dll" "-r:$managed/System.dll" "-r:$managed/System.Core.dll" (Join-Path $PSScriptRoot '../Tests/ReflectionSuite.cs') (Join-Path $PSScriptRoot 'Program.cs')
if($LASTEXITCODE -ne 0){throw 'Mono test compilation failed'}
$savedAssembly=$env:AF_TRANSPORT_PROBE_ASSEMBLY
$savedMode=$env:AF_TRANSPORT_PROBE_PATCHED
Push-Location $apf
try{
    foreach($mode in @('original','patched')){
        $env:AF_TRANSPORT_PROBE_ASSEMBLY=if($mode -eq 'original'){Join-Path $apf 'UpdateBuilds/RC40-20261103/patched/Lidgren.Network.dll'}else{Join-Path $apf 'UpdateBuilds/RC42-20261103/patched/Lidgren.Network.dll'}
        $env:AF_TRANSPORT_PROBE_PATCHED=($mode -eq 'patched').ToString()
        $output=@(& $hostExecutable $testExecutable 2>&1)
        $exitCode=$LASTEXITCODE
        [IO.File]::WriteAllLines((Join-Path $OutputDirectory ($mode+'-result.txt')),[string[]]@($output|ForEach-Object ToString))
        $output
        if($exitCode -ne 0){throw "Mono $mode tests failed: $exitCode"}
    }
}finally{
    Pop-Location
    $env:AF_TRANSPORT_PROBE_ASSEMBLY=$savedAssembly
    $env:AF_TRANSPORT_PROBE_PATCHED=$savedMode
}
