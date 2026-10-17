#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
$repository='esotericmartyristaken-bit/AETHERFRONT'
$version='kesas-rc22'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../KESAS-VPS-Server'))
. "$root/Update.Common.ps1"
$nativeFeed=Join-Path $root 'Updates/APFClientUpdate.json'
$feed=Get-Content -LiteralPath $nativeFeed -Raw|ConvertFrom-Json
if($feed.version -cne $version){throw 'Activate native RC22 before this read-only publication check.'}
# No CLI authentication or authorization headers: verify the public entry point.
$headers=@{'User-Agent'='Aetherfront-Public-Release-Check';Accept='application/vnd.github+json'}
$latest=Invoke-RestMethod "https://api.github.com/repos/$repository/releases/latest" -Headers $headers -TimeoutSec 30
if($latest.tag_name -cne $version -or $latest.draft -or $latest.prerelease){throw 'GitHub latest is not completed RC22.'}
$media=Invoke-RestMethod "https://api.github.com/repos/$repository/releases/tags/pack-media" -Headers $headers -TimeoutSec 30
$catalog=Get-Content -LiteralPath "$root/.github-prepared/$version/install-catalog.json" -Raw|ConvertFrom-Json
$packages=@($catalog.full.managedPackages)+@($catalog.potato.managedPackages)
$expectedMedia=@{}
foreach($package in $packages){
    $name=[IO.Path]::GetFileName(([uri]$package.archiveUrl).AbsolutePath)
    if($expectedMedia.ContainsKey($name)){
        if($expectedMedia[$name].sha256 -cne $package.sha256 -or $expectedMedia[$name].sizeBytes -ne $package.sizeBytes){throw 'Conflicting catalog media identity.'}
    }else{$expectedMedia[$name]=$package}
}
if($media.draft -or @($media.assets).Count -ne $expectedMedia.Count){throw 'Public media release inventory differs from the signed catalog.'}
foreach($asset in $media.assets){
    $package=$expectedMedia[$asset.name]
    if(-not $package -or $asset.state -cne 'uploaded' -or $asset.size -ne $package.sizeBytes -or $asset.digest -cne "sha256:$($package.sha256)"){throw "Public media digest/size differs: $($asset.name)"}
}
$fixtures=Join-Path $PSScriptRoot 'GitHub-Public-Verification'
if(Test-Path -LiteralPath $fixtures){Assert-UpdatePath $fixtures|Out-Null}else{[IO.Directory]::CreateDirectory($fixtures)|Out-Null}
$download=Join-Path $fixtures ([Guid]::NewGuid().ToString('N'))
if(Test-Path -LiteralPath $download){throw 'Verification directory already exists.'}
[IO.Directory]::CreateDirectory($download)|Out-Null
$expected=@{
 'APFClientUpdate.json'=$nativeFeed
 "APFClient-$version.zip"=(Join-Path $root "Updates/$version/APFClient-$version.zip")
 "Aetherfront-Starter-$version.zip"=(Join-Path $root 'KESAS-Client-Bootstrap.zip')
}
if(@($latest.assets).Count -ne $expected.Count){throw 'Unexpected version-release asset inventory.'}
foreach($asset in $latest.assets){
    if(-not $expected.ContainsKey($asset.name)){throw 'Unknown version-release asset.'}
    $trusted=$expected[$asset.name]
    $sha=(Get-FileHash -LiteralPath $trusted -Algorithm SHA256).Hash.ToLowerInvariant()
    if($asset.state -cne 'uploaded' -or $asset.size -ne (Get-Item -LiteralPath $trusted).Length -or $asset.digest -cne "sha256:$sha"){throw "Native/GitHub asset identity mismatch: $($asset.name)"}
    $url="https://github.com/$repository/releases/download/$version/$($asset.name)"
    $destination=Join-Path $download $asset.name
    # Ordinary TLS trust, no certificate bypass, no plaintext redirect allowance.
    Invoke-WebRequest $url -OutFile $destination -MaximumRedirection 5 -TimeoutSec 120
    if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sha -or (Get-Item -LiteralPath $destination).Length -ne $asset.size){throw "Anonymous downloaded bytes differ: $($asset.name)"}
    Write-Host "PASS anonymous download: $($asset.name) ($($asset.size) bytes)"
}
Invoke-UpdateTool $root @('--verify','--manifest',(Join-Path $download 'APFClientUpdate.json'),'--archive',(Join-Path $download "APFClient-$version.zip"),'--public-key-xml',"$root/secrets/update-signing-public.xml")
$report=[ordered]@{
 verifiedAt=[DateTime]::UtcNow.ToString('o');version=$version;repository=$repository
 anonymous=$true;tlsBypass=$false;nativeFeedMatches=$true;signedFiles=@($feed.files).Count
 mediaAssets=$expectedMedia.Count;mediaBytes=[long](($media.assets|Measure-Object size -Sum).Sum)
 downloadedAssets=@($latest.assets).Count
 starterPath=(Join-Path $download "Aetherfront-Starter-$version.zip")
 starterSha256=(Get-FileHash -LiteralPath (Join-Path $download "Aetherfront-Starter-$version.zip") -Algorithm SHA256).Hash.ToLowerInvariant()
 manifestPath=(Join-Path $download 'APFClientUpdate.json')
}
$report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath "$PSScriptRoot/GITHUB-LIVE-RESULT.json" -Encoding utf8
$report|ConvertTo-Json -Depth 5
