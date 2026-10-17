[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$deployment = '.\KESAS-VPS-Server'
$output = Join-Path $PSScriptRoot 'Release'
$version = 'kesas-rc14'
$utf8 = [Text.UTF8Encoding]::new($false)
if (Test-Path -LiteralPath $output) { throw 'Release output must not already exist.' }
$null = New-Item -ItemType Directory -Path $output
$baseTemplate = Join-Path $deployment 'ClientTemplate.zip'
$template = Join-Path $output 'ClientTemplate.zip'
Copy-Item -LiteralPath $baseTemplate -Destination $template

function Read-Entry($zip, [string]$name) {
    $entry = $zip.GetEntry($name)
    if (-not $entry) { throw "Missing ZIP entry: $name" }
    $stream = $entry.Open()
    $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); return ,$memory.ToArray() }
    finally { $stream.Dispose(); $memory.Dispose() }
}
function Write-Entry($zip, [string]$name, [byte[]]$bytes) {
    $entry = $zip.GetEntry($name)
    if (-not $entry) { throw "Cannot replace missing ZIP entry: $name" }
    $entry.Delete()
    $stream = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal).Open()
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
function Hash-Bytes([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

$changes = @{}
$zip = [IO.Compression.ZipFile]::Open($template, [IO.Compression.ZipArchiveMode]::Update)
try {
    $manifest = $utf8.GetString((Read-Entry $zip 'KESAS/PATCH-MANIFEST.json')) | ConvertFrom-Json
    if ($manifest.version -cne 'kesas-rc13') { throw 'Unexpected source client inventory.' }
    $settings = $utf8.GetString((Read-Entry $zip 'GameData/LunaMultiplayer/Data/APFClient.json')) | ConvertFrom-Json
    $settings.clientVersion = $version
    $policy = $utf8.GetString((Read-Entry $zip 'KESAS/Notices/FULL-PACK-POLICY.json')) | ConvertFrom-Json
    $policy.version = $version
    $changes['GameData/LunaMultiplayer/Plugins/LmpClient.dll'] = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'LmpClient-rc14.dll'))
    $changes['GameData/LunaMultiplayer/Data/APFClient.json'] = $utf8.GetBytes(($settings | ConvertTo-Json -Depth 10) + "`n")
    $changes['KESAS/Notices/FULL-PACK-POLICY.json'] = $utf8.GetBytes(($policy | ConvertTo-Json -Depth 20) + "`n")
    foreach ($name in $changes.Keys) {
        $record = @($manifest.files | Where-Object path -CEQ $name)
        if ($record.Count -ne 1) { throw "Expected one inventory record: $name" }
        Write-Entry $zip $name $changes[$name]
        $record[0].bytes = $changes[$name].Length
        $record[0].sha256 = Hash-Bytes $changes[$name]
    }
    $manifest.version = $version
    Write-Entry $zip 'KESAS/PATCH-MANIFEST.json' $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 30) + "`n")
}
finally { $zip.Dispose() }

$base = [IO.Compression.ZipFile]::OpenRead($baseTemplate)
$zip = [IO.Compression.ZipFile]::OpenRead($template)
try {
    $manifest = $utf8.GetString((Read-Entry $zip 'KESAS/PATCH-MANIFEST.json')) | ConvertFrom-Json
    if ($zip.Entries.Count -ne $base.Entries.Count -or $zip.Entries.Count -ne @($manifest.files).Count + 1) {
        throw 'Client archive entry count changed.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if (-not $seen.Add($entry.FullName) -or $entry.FullName -match '(^/|\\|:|(^|/)\.\.(/|$))') {
            throw "Unsafe/duplicate client entry: $($entry.FullName)"
        }
    }
    foreach ($record in $manifest.files) {
        $bytes = Read-Entry $zip $record.path
        if ($bytes.Length -ne $record.bytes -or (Hash-Bytes $bytes) -cne $record.sha256) {
            throw "Client inventory mismatch: $($record.path)"
        }
        if (-not $changes.ContainsKey($record.path) -and
            (Hash-Bytes (Read-Entry $base $record.path)) -cne $record.sha256) {
            throw "Unrelated client payload changed: $($record.path)"
        }
    }
}
finally { $zip.Dispose(); $base.Dispose() }

$pack = Get-Content -LiteralPath "$deployment\Server\Config\APFPack.json" -Raw | ConvertFrom-Json
if ($pack.requiredClientVersion -cne 'kesas-rc13') { throw 'Unexpected active pack version.' }
$pack.requiredClientVersion = $version
$pack.packVersion = $version
[IO.File]::WriteAllText((Join-Path $output 'APFPack-template.json'), ($pack | ConvertTo-Json -Depth 40) + "`n", $utf8)
Copy-Item -LiteralPath "$deployment\Server\Config\AetherfrontTechnology.json" -Destination $output
$files = foreach ($name in @('ClientTemplate.zip', 'APFPack-template.json', 'AetherfrontTechnology.json')) {
    [ordered]@{ path = $name; sha256 = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[IO.File]::WriteAllText((Join-Path $output 'RELEASE.json'),
    ([ordered]@{version = $version; files = @($files)} | ConvertTo-Json -Depth 10) + "`n", $utf8)
$release = Join-Path $PSScriptRoot 'KESAS-Release-kesas-rc14.zip'
$archive = [IO.Compression.ZipFile]::Open($release, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json','RELEASE.json')) {
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $output $name), $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
Write-Output "Verified all $(@($manifest.files).Count) client inventory hashes; preserved every unrelated RC13 payload."
Write-Output "Built $release"
