#requires -Version 7.0
param([Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$Output,[string]$SourceRoot=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($Version -cnotmatch '^control-[1-9][0-9]{0,8}$'){throw 'Invalid control version'}
$source=[IO.Path]::GetFullPath($SourceRoot)
$outputPath=[IO.Path]::GetFullPath($Output)
if([IO.Path]::GetFileName($outputPath) -cne "Aetherfront-Control-$Version.zip" -or [IO.File]::Exists($outputPath)){throw 'Use a new immutable control archive path'}
$names=@(
    @('worker','engine','contract','github','public-status','native-agent','broker-host','host-reporter') | ForEach-Object {"ControlPlane/$_.mjs"}
    'ControlPlane/Invoke-NativePublisher.ps1'
    'ControlPlane/Read-ClientInventory.ps1'
    Get-ChildItem -LiteralPath "$source/APFDiscordBot/src" -File | Where-Object Name -cmatch '^[a-z][a-z0-9-]*\.js$' | ForEach-Object {"APFDiscordBot/src/$($_.Name)"}
    'APFDiscordBot/package.json';'APFDiscordBot/package-lock.json'
    @('Verify-KesasRelease','Stop-KesasGracefully','Stop-APFLmpGracefully') | ForEach-Object {"APFDiscordBot/scripts/$_.ps1"}
    @('Publish-Release','Update.Common','GitHub.Publish','Export-Client','Start-Server','Stop-Server') | ForEach-Object {"KESAS-VPS-Server/$_.ps1"}
) | Sort-Object -CaseSensitive
$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath))
for($cursor=$outputPath;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
    if((Test-Path -LiteralPath $cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe bundle output path'}
}
$zip=[IO.Compression.ZipFile]::Open($outputPath,[IO.Compression.ZipArchiveMode]::Create)
try {
    $files=@(foreach($name in $names) {
        $full=Join-Path $source $name
        for($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
            if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe bundle source path'}
        }
        $bytes=[IO.File]::ReadAllBytes($full)
        $entry=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
        $stream=$entry.Open();try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
        [ordered]@{path=$name;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant();sizeBytes=$bytes.Length}
    })
    $manifest=[ordered]@{schemaVersion=1;transactionVersion=1;version=$Version;runtime=[ordered]@{nodeMajor=24;powerShellMajor=7;dotnetMajor=8};capabilities=@('application-v1','media-payload-v1','control-plane-v1','host-report-v1','guard-15m-empty-v1','atomic-control-rollback-v1','native-tools-v1','versioned-broker-v1','independent-report-v1','unattended-host-v1','media-reference-v1');files=$files}
    $entry=$zip.CreateEntry('CONTROL.json',[IO.Compression.CompressionLevel]::Optimal)
    $writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false))
    try{$writer.Write(($manifest|ConvertTo-Json -Depth 8 -Compress))}finally{$writer.Dispose()}
} finally {$zip.Dispose()}
$descriptor=[ordered]@{schemaVersion=1;transactionVersion=1;version=$Version;releaseTag="host-$Version";assetName=[IO.Path]::GetFileName($outputPath);sha256=(Get-FileHash -LiteralPath $outputPath).Hash.ToLowerInvariant();sizeBytes=([IO.FileInfo]$outputPath).Length;minimumBootstrap=1}
$descriptor|ConvertTo-Json -Compress
