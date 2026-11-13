#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$evidence='C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC64-FullChain-20261113'
$Output=[IO.Path]::GetFullPath($Output)
if(!(Split-Path $Output).Equals($evidence,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $Output)){throw 'Fresh evidence child required'}
$null=New-Item -ItemType Directory -Path $Output
$dotnet='C:\Users\Administrator\Desktop\APF\tools\dotnet-sdk8\dotnet.exe'
$game='C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke'
$managed=Join-Path $game 'KSP_x64_Data\Managed'
$cecil='C:\Users\Administrator\Desktop\APF\tools\dotnet-sdk6\sdk\6.0.428\Sdks\Microsoft.NET.ILLink.Tasks\tools\net6.0\Mono.Cecil.dll'
$runtime=Join-Path $evidence 'Baseline\Server'
$plugins=Join-Path $evidence 'Baseline\Client\GameData\LunaMultiplayer\Plugins'
$candidate=Join-Path $Output 'Server'
$client=Join-Path $Output 'Client'
$null=New-Item -ItemType Directory -Path $candidate,$client
Get-ChildItem -LiteralPath $runtime -File | Where-Object Name -NotIn 'Server.dll','AetherfrontContinuityServer.dll' | Copy-Item -Destination $candidate
Get-ChildItem -LiteralPath (Join-Path $evidence 'Client-v2') -Filter '*.dll' | Where-Object Name -NotIn 'LmpClient.dll','AetherfrontPersonalClient.dll','AetherfrontStabilityClient.dll' | Copy-Item -Destination $client
function Run([string]$Name,[string[]]$Arguments){
 & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $Output ($Name+'.log'))
 if($LASTEXITCODE){throw "$Name failed: $LASTEXITCODE"}
}
function Build([string]$Name,[string]$Project,[string[]]$Properties=@()){
 $destination=Join-Path $Output $Name
 Run $Name (@('build',(Join-Path $repo $Project),'-c','Release','-o',$destination,'--nologo','-v','quiet')+$Properties)
}
Build 'Patch' 'Aetherfront-RC64-Work\Patch\Patch.csproj' @("-p:CecilPath=$cecil")
Build 'Donor' 'Aetherfront-RC56-Work\Server\Server.csproj' @("-p:Runtime=$runtime",'-p:NoWarn=0436')
Build 'ClockDonor' 'Aetherfront-RC64-Work\Clock\Clock.csproj' @("-p:Runtime=$runtime","-p:References=$(Join-Path $evidence 'References')",'-p:NoWarn=0436')
Build 'ResearchDonor' 'Aetherfront-RC46-Work\Client\ResearchSync.csproj' @("-p:ClientPlugins=$plugins","-p:KspManaged=$managed",'-p:NoWarn=0436')
$patch=Join-Path $Output 'Patch\Patch.dll'
$env:AETHERFRONT_KSP_MANAGED=$managed
Run 'ServerGraft' @($patch,'server',(Join-Path $runtime 'Server.dll'),(Join-Path $Output 'Donor\SynchronizationDonor.dll'),(Join-Path $candidate 'Server.dll'))
Run 'ClockGraft' @($patch,'clock',(Join-Path $runtime 'AetherfrontContinuityServer.dll'),(Join-Path $Output 'ClockDonor\ClockDonor.dll'),(Join-Path $candidate 'AetherfrontContinuityServer.dll'))
Run 'ClientGraft' @($patch,'client',(Join-Path $plugins 'LmpClient.dll'),'-',(Join-Path $client 'LmpClient.dll'))
Run 'ResearchGraft' @($patch,'personal',(Join-Path $plugins 'AetherfrontPersonalClient.dll'),(Join-Path $Output 'ResearchDonor\AetherfrontResearchSync.dll'),(Join-Path $client 'AetherfrontPersonalClient.dll'))
Run 'Stability' @('build',(Join-Path $repo 'Aetherfront-RC56-Work\Client\Client.csproj'),'-c','Release','-o',$client,'--nologo','-v','quiet',"-p:ClientPlugins=$client","-p:KspManaged=$managed","-p:HarmonyPath=$(Join-Path $game 'GameData\000_Harmony\0Harmony.dll')")
Build 'JournalTests' 'Aetherfront-RC64-Work\Tests\Tests.csproj' @("-p:Candidate=$candidate","-p:Runtime=$runtime")
Build 'ClockTests' 'Aetherfront-RC64-Work\ClockTests\ClockTests.csproj' @("-p:Candidate=$candidate","-p:Runtime=$runtime")
$stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
Run 'JournalExecution' @((Join-Path $Output 'JournalTests\Tests.dll'),(Join-Path $evidence "Tests-Journal-$stamp"))
Run 'ClockExecution' @((Join-Path $Output 'ClockTests\ClockTests.dll'),(Join-Path $evidence "Tests-Clock-$stamp"),$candidate)
$fixture=Join-Path 'C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture' ("RC64-$stamp")
$null=New-Item -ItemType Directory -Path $fixture
Get-ChildItem -LiteralPath $client -Filter '*.dll' | Copy-Item -Destination $fixture
Run 'MonoBuild' @('build',(Join-Path $repo 'Aetherfront-RC56-Work\MonoTests\MonoTests.csproj'),'-c','Release','-o',$fixture,'--nologo','-v','quiet',"-p:Fixture=$fixture","-p:KspManaged=$managed")
Run 'ResearchMonoBuild' @('build',(Join-Path $repo 'Aetherfront-RC64-Work\ResearchTests\ResearchMonoTests.csproj'),'-c','Release','-o',$fixture,'--nologo','-v','quiet',"-p:Client=$fixture")
$env:RC56_KSP_REFERENCE=$game;$env:RC56_MONO_ROOT=$fixture
foreach($test in 'MonoTests','ResearchMonoTests'){
 & 'C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\MonoHost\MonoHost.exe' (Join-Path $fixture ($test+'.exe')) 2>&1 | Tee-Object -FilePath (Join-Path $Output ($test+'Execution.log'))
 if($LASTEXITCODE){throw "$test failed: $LASTEXITCODE"}
}
[ordered]@{completedUtc=[DateTime]::UtcNow.ToString('O');output=$Output;fixture=$fixture;shippingFiles=@(Get-ChildItem -LiteralPath $candidate,$client -Filter '*.dll' | Where-Object Name -In 'Server.dll','AetherfrontContinuityServer.dll','LmpClient.dll','AetherfrontPersonalClient.dll','AetherfrontStabilityClient.dll' | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}});renderedFlightTest=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'Validated.json')
