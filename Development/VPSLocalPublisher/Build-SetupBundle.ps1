#requires -Version 7.5
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$names=@('Install-VPSLocalPublisher.ps1','Worker.mjs','LocalReleaseRequest.mjs','Invoke-VPSLocalPublisher.ps1','Publisher.Common.ps1','Read-JobCredential.ps1','Assert-LocalRelease.ps1','SessionAccess.cs','Approve-LocalRelease.ps1','Sign-LocalReleaseRequest.mjs','Get-PublisherStatus.ps1')
$manifest=@{schemaVersion=3;files=@()}
$full=[IO.Path]::GetFullPath($OutputPath)
$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
$zip=[IO.Compression.ZipFile]::Open($full,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($name in @($names)+@('Verify-KesasRelease.ps1')){
        $path=if($name -ceq 'Verify-KesasRelease.ps1'){Join-Path $sourceRoot 'APFDiscordBot/scripts/Verify-KesasRelease.ps1'}else{Join-Path $PSScriptRoot $name}
        $bytes=[IO.File]::ReadAllBytes($path)
        $entry=$zip.CreateEntry($name);$stream=$entry.Open();try{$stream.Write($bytes)}finally{$stream.Dispose()}
        $manifest.files+=@{name=$name;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()}
    }
    $entry=$zip.CreateEntry('setup-manifest.json');$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false));try{$writer.Write(($manifest|ConvertTo-Json -Depth 5))}finally{$writer.Dispose()}
} finally {$zip.Dispose()}
[pscustomobject]@{path=$full;sha256=(Get-FileHash -LiteralPath $full).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $full).Length;files=$manifest.files.Count}
