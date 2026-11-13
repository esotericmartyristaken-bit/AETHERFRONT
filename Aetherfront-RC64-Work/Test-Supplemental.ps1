#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Validated)
$ErrorActionPreference='Stop'
$proof=Get-Content -LiteralPath (Join-Path $Validated 'Validated.json') -Raw | ConvertFrom-Json
$fixture=$proof.fixture;$repo=Split-Path $PSScriptRoot
$game='C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke'
$dotnet='C:\Users\Administrator\Desktop\APF\tools\dotnet-sdk8\dotnet.exe'
$managed=Join-Path $game 'KSP_x64_Data\Managed'
function Build([string]$Project,[string[]]$Properties){
 & $dotnet build (Join-Path $repo $Project) -c Release -o $fixture --nologo -v quiet @Properties
 if($LASTEXITCODE){throw "Build failed: $Project"}
}
Build 'Aetherfront-RC56-Work\MonoTests\MonoTests.csproj' @("-p:Fixture=$fixture","-p:KspManaged=$managed")
Build 'Aetherfront-RC64-Work\ResearchTests\ResearchMonoTests.csproj' @("-p:Client=$fixture")
Build 'Aetherfront-RC56-Work\PersistentTests\PersistentTests.csproj' @("-p:Fixture=$fixture","-p:KspManaged=$managed","-p:GameRoot=$game")
Build 'Aetherfront-RC63-Work\StartupTests\StartupTests.csproj' @("-p:Engine=$fixture","-p:Plugins=$fixture")
$env:RC56_KSP_REFERENCE=$game;$env:RC56_MONO_ROOT=$fixture;$env:RC61_CLIENT_VERSION='kesas-rc64'
$env:RC63_STARTUP_ROOT=Join-Path (Split-Path $fixture) ('RC64-Startup-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
foreach($test in 'MonoTests','ResearchMonoTests','PersistentTests','StartupTests'){
 & 'C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\MonoHost\MonoHost.exe' (Join-Path $fixture ($test+'.exe')) 2>&1 | Tee-Object -FilePath (Join-Path $Validated ($test+'Final.log'))
 if($LASTEXITCODE){throw "$test failed: $LASTEXITCODE"}
}
& $dotnet 'C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\RC63-Startup\Audit-Final\Audit.dll' (Join-Path $fixture 'KesasInstall.dll') (Join-Path $fixture 'KesasInstall.dll') $fixture $managed 2>&1 | Tee-Object -FilePath (Join-Path $Validated 'InstallerApiFinal.log')
if($LASTEXITCODE){throw 'Installer API audit failed'}
foreach($row in $proof.shippingFiles){if((Get-FileHash -LiteralPath $row.path).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Shipping file changed during tests'}}
[ordered]@{completedUtc=[DateTime]::UtcNow.ToString('O');shippingFilesUnchanged=$true;actualKspMono=$true;renderedFlightTest=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Validated 'Supplemental.json')
