$ErrorActionPreference='Stop'
$root='.\KESAS-VPS-Server'
. "$root/Update.Common.ps1"
$output=Join-Path $PSScriptRoot 'Candidate'
if(Test-Path -LiteralPath $output){throw 'Candidate already exists.'}
Expand-UpdateArchive "$PSScriptRoot/Release/ClientTemplate.zip" "$output/Client"
New-UpdateDirectory "$output/Published"
Invoke-UpdateTool $root @('--root-overlay','--source-root',"$output/Client",'--output',"$output/Published",'--version','kesas-rc22','--package-base-url','https://217.216.49.250:18866/updates/kesas-rc22/','--private-key-xml',"$root/secrets/update-signing-private.xml",'--ksp-version','1.12.5','--release-notes','GitHub-hosted downloads and client update checks when connecting.')
Invoke-UpdateTool $root @('--verify','--manifest',"$output/Published/APFClientUpdate.json",'--archive',"$output/Published/APFClient-kesas-rc22.zip",'--public-key-xml',"$root/secrets/update-signing-public.xml")
