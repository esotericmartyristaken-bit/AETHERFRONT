#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../KESAS-VPS-Server'))
. "$root/Update.Common.ps1"
. "$root/GitHub.Publish.ps1"
Invoke-UpdateTool $root @('--verify','--manifest',"$PSScriptRoot/Candidate/Published/APFClientUpdate.json",'--archive',"$PSScriptRoot/Candidate/Published/APFClient-kesas-rc23.zip",'--public-key-xml',"$root/secrets/update-signing-public.xml")
$plan=New-GitHubPublicationPlan -Repository 'esotericmartyristaken-bit/AETHERFRONT' -MediaTag 'pack-media' -PublishedDirectory "$PSScriptRoot/Candidate/Published" -BootstrapZip "$PSScriptRoot/Candidate/Aetherfront-Starter-kesas-rc23.zip" -CatalogPath "$PSScriptRoot/Candidate/Client/GameData/LunaMultiplayer/Launcher/install-catalog.json" -MediaDirectory "$root/Server/Config"
Write-UpdateAtomic "$PSScriptRoot/GitHub-Media-Plan.json" ($plan | ConvertTo-Json -Depth 12)
Write-Output "Preparing $($plan.MediaAssets.Count) immutable media assets. Existing 217 assets are retained; only official 4K content is new. No release activation or restart."
$result=Prepare-GitHubMedia -Plan $plan
Write-UpdateAtomic "$PSScriptRoot/GitHub-Media-Result.json" ($result | ConvertTo-Json -Depth 8)
$result | ConvertTo-Json -Depth 8
