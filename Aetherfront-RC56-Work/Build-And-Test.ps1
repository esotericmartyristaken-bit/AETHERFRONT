#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Fixture = 'C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture',
    [string]$RunName = ('Verified-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')),
    [string]$Ksp = 'C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke',
    [string]$Dotnet = 'C:\Users\Administrator\Desktop\APF\tools\dotnet-sdk8\dotnet.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Fixture = [IO.Path]::GetFullPath($Fixture).TrimEnd('\')
if ($Fixture -cne 'C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture' -or
    $RunName -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,79}$') { throw 'An isolated, fresh RC56 fixture is required.' }
$run = Join-Path $Fixture $RunName
if (Test-Path -LiteralPath $run) { throw 'The run directory already exists; previous evidence must not be overwritten.' }
$baseline = Join-Path $Fixture 'Baseline'
$plugins = Join-Path $Fixture 'Client\GameData\LunaMultiplayer\Plugins'
$managed = Join-Path $Ksp 'KSP_x64_Data\Managed'
$harmony = Join-Path $Ksp 'GameData\000_Harmony\0Harmony.dll'
$pins = @{
    (Join-Path $baseline 'Server.dll') = '08f8de5de2b4d50fa3e8314e35e821c347bb2d0a7f7f323526519cd8c538db36'
    (Join-Path $baseline 'AetherfrontContinuityServer.dll') = 'f940d7aebd4769582c16b546378cfb91e0f9047af6ffe57768929f6f7a94c5e1'
    (Join-Path $plugins 'LmpClient.dll') = '58dcfafb7a930cf1d1fcfe19be4780037becf2b277341e7d586307be5ff4f659'
}
foreach ($entry in $pins.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.Value) {
        throw 'A shipping baseline does not match the reviewed RC55 identity.'
    }
}
$null = New-Item -ItemType Directory -Path $run
function Invoke-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed: ' + $Arguments[0]) }
}
function Build([string]$Project, [string]$Output, [string[]]$Properties = @()) {
    Invoke-Dotnet (@('build', (Join-Path $PSScriptRoot $Project), '-c', 'Release', '-o', (Join-Path $run $Output), '--nologo', '-v', 'quiet') + $Properties)
}
Build 'Server\Server.csproj' 'Donor' @("-p:Runtime=$baseline")
Build 'Patch\Patch.csproj' 'Patch'
$patch = Join-Path $run 'Patch\Patch.dll'
$server = Join-Path $run 'Server'
Invoke-Dotnet @($patch, 'server', (Join-Path $baseline 'Server.dll'), (Join-Path $run 'Donor\SynchronizationDonor.dll'), (Join-Path $server 'Server.dll')) | Out-Null
Invoke-Dotnet @($patch, 'clock', (Join-Path $baseline 'AetherfrontContinuityServer.dll'), (Join-Path $server 'Server.dll'), (Join-Path $server 'AetherfrontContinuityServer.dll')) | Out-Null
Invoke-Dotnet @($patch, 'personal', (Join-Path $baseline 'AetherfrontPersonalServer.dll'), (Join-Path $server 'Server.dll'), (Join-Path $server 'AetherfrontPersonalServer.dll')) | Out-Null
Get-ChildItem -LiteralPath $baseline -Filter '*.dll' -File |
    Where-Object Name -NotIn @('Server.dll', 'AetherfrontContinuityServer.dll', 'AetherfrontPersonalServer.dll', 'System.Private.CoreLib.dll') |
    Copy-Item -Destination $server
$client = Join-Path $run 'Client'
$null = New-Item -ItemType Directory -Path $client
Get-ChildItem -LiteralPath $plugins -Filter '*.dll' -File | Where-Object Name -NotIn @('LmpClient.dll','AetherfrontStabilityClient.dll') | Copy-Item -Destination $client
Invoke-Dotnet @($patch, 'client', (Join-Path $plugins 'LmpClient.dll'), '-', (Join-Path $client 'LmpClient.dll'), $managed) | Out-Null
Build 'Client\Client.csproj' 'Client' @("-p:ClientPlugins=$plugins", "-p:KspManaged=$managed", "-p:HarmonyPath=$harmony")
Copy-Item -LiteralPath $harmony -Destination $client
Build 'Tests\Tests.csproj' 'Tests' @("-p:Candidate=$server", "-p:Runtime=$server")
Invoke-Dotnet @((Join-Path $run 'Tests\Tests.dll'), (Join-Path $run 'Regression'), $server, (Join-Path $Fixture 'CrewSeed-20261111'))
Build 'MonoHost\MonoHost.csproj' 'MonoHost'
Build 'MonoTests\MonoTests.csproj' 'Client' @("-p:Fixture=$client", "-p:KspManaged=$managed")
$env:RC56_KSP_REFERENCE = $Ksp
$env:RC56_MONO_ROOT = $client
& (Join-Path $run 'MonoHost\MonoHost.exe') (Join-Path $client 'MonoTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Actual KSP Mono validation failed.' }
Write-Output ('Verified isolated candidate: ' + $run)
Write-Output 'No release archive, signature, publication, task change, production restart or authenticated Unity flight test was performed.'
