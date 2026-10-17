param([Parameter(Mandatory=$true)][string]$Archive,[Parameter(Mandatory=$true)][string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
try {
    $entry = $zip.GetEntry('RELEASE.json')
    if (-not $entry -or $entry.Length -gt 65536) { throw 'Missing or oversized RELEASE.json' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $release = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($release.version -cne $ExpectedVersion) { throw 'Release version does not match staged version' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $release.files) {
        if (-not $seen.Add($file.path) -or $file.path -match '(^/|\\|:|(^|/)\.\.(/|$))' -or
            ($file.path -cnotin @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json') -and
             $file.path -cnotmatch '^(ServerPayload|PackHostPayload|CertificateToolPayload)/')) { throw 'Unsafe release path' }
        if ($file.path -match '^(ServerPayload|PackHostPayload|CertificateToolPayload)/(Config|Universe|Aetherfront|Logs|secrets)(/|$)') { throw 'Protected server state in release' }
        $item = $zip.GetEntry($file.path)
        if (-not $item -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Missing release entry/hash' }
        $stream = $item.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($hash -ine $file.sha256) { throw "Release hash mismatch: $($file.path)" }
    }
    if ($zip.Entries.Count -ne $seen.Count + 1) { throw 'Undeclared or duplicate release entries' }
    foreach ($name in @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')) {
        if (-not $seen.Contains($name)) { throw "Missing required release file: $name" }
    }
    Write-Output "Verified $ExpectedVersion"
}
finally { $zip.Dispose() }
