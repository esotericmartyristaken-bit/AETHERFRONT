#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$apf = Split-Path (Split-Path $repo)
$baseline = Join-Path $apf 'UpdateBuilds\RC64-FullChain-20261113\Build-v5'
$runtime = Join-Path $baseline 'Server'
$plugins = Join-Path $baseline 'Client'
$managed = Join-Path $apf 'Aetherfront-KSP-Smoke\KSP_x64_Data\Managed'
$dotnet = Join-Path $apf 'tools\dotnet-sdk8\dotnet.exe'
$cecil = Join-Path $apf 'tools\dotnet-sdk6\sdk\6.0.428\Sdks\Microsoft.NET.ILLink.Tasks\tools\net6.0\Mono.Cecil.dll'
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output -ErrorAction Stop) { throw 'Use a fresh output directory' }
if ((Get-FileHash (Join-Path $plugins 'LmpClient.dll')).Hash -ne 'eee970ac7e1cea2e3b70b66fe331a04f26edfaeefc878af165b9064cdef8520e') { throw 'Unexpected RC64 client baseline' }
$null = New-Item -ItemType Directory -Path $Output
$env:AETHERFRONT_KSP_MANAGED = $managed
function Run([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE) { throw "Build/test command exited $LASTEXITCODE" }
}
Run @('build', (Join-Path $repo 'Aetherfront-RC64-Work\Patch\Patch.csproj'), '-c','Release','-o',"$Output\Patch","-p:CecilPath=$cecil")
Run @("$Output\Patch\Patch.dll",'tracking-reference',"$runtime\Server.dll",'-',"$Output\Reference\Server.dll")
Run @('build',"$PSScriptRoot\Server\Server.csproj",'-c','Release','-o',"$Output\Donor","-p:Runtime=$runtime","-p:ServerReference=$Output\Reference\Server.dll")
Run @("$Output\Patch\Patch.dll",'tracking',"$runtime\Server.dll","$Output\Donor\Server.dll","$Output\Candidate\Server.dll")
Run @('build',"$PSScriptRoot\Client\Client.csproj",'-c','Release','-o',"$Output\Client","-p:KspManaged=$managed","-p:ClientPlugins=$plugins")
Run @('build',"$PSScriptRoot\Tests\Tests.csproj",'-c','Release','-o',"$Output\Tests","-p:Candidate=$Output\Candidate","-p:Runtime=$runtime")
Get-ChildItem -LiteralPath $runtime -Filter '*.dll' -File | Where-Object Name -NE 'Server.dll' | ForEach-Object {
    $dest = Join-Path "$Output\Tests" $_.Name
    if (!(Test-Path -LiteralPath $dest)) { Copy-Item -LiteralPath $_.FullName -Destination $dest }
}
Run @("$Output\Tests\Tests.dll")
$monoRoot = Join-Path $apf 'UpdateBuilds\RC56-Sync-Fixture'
$fixture = Join-Path $monoRoot ('Tracking-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
Get-ChildItem -LiteralPath $plugins -Filter '*.dll' -File | Copy-Item -Destination $fixture
Copy-Item -LiteralPath "$Output\Client\AetherfrontTrackingClient.dll" -Destination $fixture
Run @('build',"$PSScriptRoot\ClientTests\ClientTests.csproj",'-c','Release','-o',$fixture,"-p:KspManaged=$managed","-p:Fixture=$fixture")
$env:RC56_KSP_REFERENCE = Join-Path $apf 'Aetherfront-KSP-Smoke'
$env:RC56_MONO_ROOT = $fixture
& (Join-Path $monoRoot 'MonoHost\MonoHost.exe') (Join-Path $fixture 'ClientTests.exe')
if ($LASTEXITCODE) { throw "KSP Mono test exited $LASTEXITCODE" }
