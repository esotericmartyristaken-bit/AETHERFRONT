[CmdletBinding()]
param([string]$Repository='esotericmartyristaken-bit/AETHERFRONT')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath("$PSScriptRoot/../KESAS-VPS-Server")
. "$root/Update.Common.ps1"
. "$PSScriptRoot/GitHub.Publish.ps1"
$catalogPath="$PSScriptRoot/Candidate/Client/GameData/LunaMultiplayer/Launcher/install-catalog.json"
$catalog=Read-UpdateText $catalogPath|ConvertFrom-Json
if(-not $catalog.gitHub.enabled -or $catalog.gitHub.repository -cne $Repository){throw 'Candidate is not configured for this GitHub repository.'}
Invoke-UpdateTool $root @('--verify','--manifest',"$PSScriptRoot/Candidate/Published/APFClientUpdate.json",'--archive',"$PSScriptRoot/Candidate/Published/APFClient-kesas-rc22.zip",'--public-key-xml',"$root/secrets/update-signing-public.xml")
$bootstrap="$root/Aetherfront-RC22-TestExport.zip"
& "$root/Export-Client.ps1" -Template "$PSScriptRoot/Release/ClientTemplate.zip" -Output $bootstrap
Write-Output 'Verifying the complete media inventory. Services remain running.'
$plan=New-GitHubPublicationPlan -Repository $Repository -MediaTag $catalog.gitHub.mediaTag -PublishedDirectory "$PSScriptRoot/Candidate/Published" -BootstrapZip $bootstrap -CatalogPath $catalogPath -MediaDirectory "$root/Server/Config"
Write-UpdateAtomic "$PSScriptRoot/GitHub-Media-Plan.json" ($plan|ConvertTo-Json -Depth 12)
$total=[long]0;foreach($asset in $plan.MediaAssets){$total+=$asset.Size}
Write-Output "Preparing $($plan.MediaAssets.Count) immutable media assets ($total bytes). No client version draft or restart."
$result=Prepare-GitHubMedia -Plan $plan
Write-UpdateAtomic "$PSScriptRoot/GitHub-Media-Result.json" ($result|ConvertTo-Json -Depth 8)
$result|ConvertTo-Json -Depth 8
