#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$four = Join-Path $PSScriptRoot 'Kcalbeloh.Textures-4k.zip'
$eight = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../KESAS-VPS-Server/Server/Config/kesas-kcalbelohtextures-44097afb-36be531daf64fe9a.zip'))
function Read-Inventory([string]$path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    $items = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $name = $entry.FullName
            if ($name.StartsWith('GameData/', [StringComparison]::Ordinal)) { $name = $name.Substring(9) }
            if ($name -notmatch '^KcalbelohTextures/' -or $name.Contains('\') -or $name -match '(^|/)\.\.?(/|$)|:') { throw 'Unexpected texture archive path.' }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            $width = 0; $height = 0
            if ($name.EndsWith('.dds', [StringComparison]::OrdinalIgnoreCase)) {
                $stream = $entry.Open()
                try {
                    $header = [byte[]]::new(24); $offset = 0
                    while ($offset -lt 24) { $count = $stream.Read($header, $offset, 24-$offset); if (!$count) { break }; $offset += $count }
                    if ($offset -ne 24 -or [Text.Encoding]::ASCII.GetString($header,0,4) -cne 'DDS ') { throw 'Invalid DDS header.' }
                    $width = [BitConverter]::ToInt32($header,16); $height = [BitConverter]::ToInt32($header,12)
                } finally { $stream.Dispose() }
            }
            $items.Add($name, [pscustomobject]@{path=$name;sizeBytes=$entry.Length;sha256=$hash;width=$width;height=$height})
        }
    } finally { $zip.Dispose() }
    return ,$items
}
$inventory8 = Read-Inventory $eight
$inventory4 = Read-Inventory $four
$missing = @($inventory8.Keys | Where-Object { !$inventory4.ContainsKey($_) })
$added = @($inventory4.Keys | Where-Object { !$inventory8.ContainsKey($_) })
$changed = @(foreach ($name in $inventory4.Keys) {
    if ($inventory8.ContainsKey($name) -and $inventory4[$name].sha256 -cne $inventory8[$name].sha256) {
        [pscustomobject]@{path=$name;old=$inventory8[$name];new=$inventory4[$name]}
    }
})
$gameplayChanges = @($changed | Where-Object path -Match '(?i)(height|biome|\.cfg$|\.dll$|\.bin$)')
$report = [ordered]@{
    schemaVersion=1;createdAtUtc=[DateTime]::UtcNow.ToString('o');officialVersion='v1.1.8'
    official4kSource='https://github.com/jcyuan06/Kcalbeloh-System/releases/download/v1.1.8/Kcalbeloh.Textures-4k.zip'
    archive4kSha256=(Get-FileHash -LiteralPath $four -Algorithm SHA256).Hash.ToLowerInvariant()
    archive8kSha256=(Get-FileHash -LiteralPath $eight -Algorithm SHA256).Hash.ToLowerInvariant()
    files4k=$inventory4.Count;files8k=$inventory8.Count
    expandedBytes4k=($inventory4.Values | Measure-Object sizeBytes -Sum).Sum
    expandedBytes8k=($inventory8.Values | Measure-Object sizeBytes -Sum).Sum
    missingPaths=$missing;addedPaths=$added;changedCount=$changed.Count;gameplayChanges=$gameplayChanges
    changed=$changed;inventory4k=@($inventory4.Values | Sort-Object path);inventory8k=@($inventory8.Values | Sort-Object path)
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'Kcalbeloh-Comparison.json'), ($report | ConvertTo-Json -Depth 12)+"`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{Files4k=$inventory4.Count;Files8k=$inventory8.Count;Missing=$missing.Count;Added=$added.Count;Changed=$changed.Count;GameplayChanged=$gameplayChanges.Count;Expanded4k=$report.expandedBytes4k;Expanded8k=$report.expandedBytes8k;ChangedPaths=@($changed.path)} | ConvertTo-Json -Depth 4
if ($missing.Count -or $added.Count -or $gameplayChanges.Count) { throw 'Archive differences require explicit review before publication.' }
