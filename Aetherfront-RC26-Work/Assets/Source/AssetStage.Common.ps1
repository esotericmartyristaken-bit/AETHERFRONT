# Shared packaging primitives. No downloads, extraction, signing, or live writes.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-Asset([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-AssetHash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-AssetBytesHash([byte[]]$Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-AssetStreamHash($Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToHexString($sha.ComputeHash($Stream)).ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Assert-AssetNoReparse([string]$Path) {
    for ($probe = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path)); $null -ne $probe; $probe = $probe.Directory) {
        if ($probe.Exists -and ($probe.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a reparse-point path: $($probe.FullName)"
        }
        if ($probe -is [IO.DirectoryInfo]) { break }
    }
    $directory = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path)))
    for (; $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a reparse-point ancestor: $($directory.FullName)"
        }
    }
}

function Resolve-AssetInput([string]$Workspace, [string]$Relative) {
    Assert-Asset (!$Relative.Contains('..') -and ![IO.Path]::IsPathRooted($Relative)) 'Input paths must be workspace-relative and contain no traversal.'
    $full = [IO.Path]::GetFullPath((Join-Path $Workspace $Relative))
    Assert-Asset ($full.StartsWith($Workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Input escaped the workspace.'
    Assert-AssetNoReparse $full
    Assert-Asset ([IO.File]::Exists($full)) "Missing pinned input: $full"
    $full
}

function Resolve-AssetStage([string]$Assets, [string]$OutputDirectory) {
    $full = [IO.Path]::GetFullPath($OutputDirectory)
    Assert-Asset ([IO.Path]::GetDirectoryName($full).Equals($Assets, [StringComparison]::OrdinalIgnoreCase)) 'Stage must be an immediate child of RC26 Assets.'
    Assert-Asset ([IO.Path]::GetFileName($full) -cmatch '^Staged(?:-[A-Za-z0-9-]+)?$') 'Only Assets/Staged or a new Assets/Staged-<name> is allowed.'
    Assert-AssetNoReparse $full
    $full
}

function Assert-AssetEntryName([string]$Name, [string]$Root) {
    Assert-Asset ($Name.StartsWith($Root + '/', [StringComparison]::Ordinal)) "Wrong archive root: $Name"
    Assert-Asset ($Name -notmatch '\\|(^|/)\.\.?(/|$)|:|[\x00-\x1f]') "Unsafe archive entry: $Name"
}

function Read-AssetRange([string]$Path, [long]$Offset, [int]$Count) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        [void]$stream.Seek($Offset, [IO.SeekOrigin]::Begin)
        $bytes = [byte[]]::new($Count)
        $stream.ReadExactly($bytes, 0, $Count)
        return ,$bytes
    } finally { $stream.Dispose() }
}

function Get-AssetZipLayout([string]$Path) {
    $length = [IO.FileInfo]::new($Path).Length
    Assert-Asset ($length -ge 22 -and $length -lt [uint32]::MaxValue) 'Only bounded, non-ZIP64 archives are supported.'
    $tailLength = [int][Math]::Min(65557, $length)
    $tail = Read-AssetRange $Path ($length - $tailLength) $tailLength
    $found = -1
    for ($i = $tail.Length - 22; $i -ge 0; $i--) {
        if ([BitConverter]::ToUInt32($tail, $i) -eq 0x06054b50 -and $i + 22 + [BitConverter]::ToUInt16($tail, $i + 20) -eq $tail.Length) { $found = $i; break }
    }
    Assert-Asset ($found -ge 0) 'No exact terminal ZIP end record.'
    $count = [BitConverter]::ToUInt16($tail, $found + 10)
    $size = [BitConverter]::ToUInt32($tail, $found + 12)
    $offset = [BitConverter]::ToUInt32($tail, $found + 16)
    $endOffset = $length - $tailLength + $found
    Assert-Asset ([BitConverter]::ToUInt16($tail, $found + 4) -eq 0 -and [BitConverter]::ToUInt16($tail, $found + 6) -eq 0) 'Multi-disk ZIP is forbidden.'
    Assert-Asset ([BitConverter]::ToUInt16($tail, $found + 8) -eq $count -and $count -lt 65535) 'ZIP64 or inconsistent entry counts are forbidden.'
    Assert-Asset ([long]$offset + $size -eq $endOffset) 'Unexpected central-directory layout.'
    $central = Read-AssetRange $Path $offset ([int]$size)
    $records = [Collections.Generic.List[object]]::new()
    $position = 0
    for ($i = 0; $i -lt $count; $i++) {
        Assert-Asset ($position + 46 -le $central.Length -and [BitConverter]::ToUInt32($central, $position) -eq 0x02014b50) 'Invalid central-directory entry.'
        $recordLength = 46 + [BitConverter]::ToUInt16($central, $position + 28) + [BitConverter]::ToUInt16($central, $position + 30) + [BitConverter]::ToUInt16($central, $position + 32)
        Assert-Asset ($position + $recordLength -le $central.Length) 'Truncated central-directory entry.'
        Assert-Asset (([BitConverter]::ToUInt16($central, $position + 8) -band 1) -eq 0) 'Encrypted ZIP entries are forbidden.'
        Assert-Asset ([BitConverter]::ToUInt16($central, $position + 34) -eq 0) 'Multi-disk entry is forbidden.'
        $record = [byte[]]::new($recordLength)
        [Array]::Copy($central, $position, $record, 0, $recordLength)
        $records.Add($record)
        $position += $recordLength
    }
    Assert-Asset ($position -eq $central.Length) 'Unrecognized central-directory trailing metadata.'
    $commentLength = [BitConverter]::ToUInt16($tail, $found + 20)
    $comment = [byte[]]::new($commentLength)
    [Array]::Copy($tail, $found + 22, $comment, 0, $commentLength)
    [pscustomobject]@{ length = $length; count = $count; centralOffset = [long]$offset; centralBytes = $central; records = $records; comment = $comment }
}

function Get-AssetEntryBytes($Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    Assert-Asset ($null -ne $entry) "Missing exact source entry: $Name"
    $stream = $entry.Open(); $buffer = [IO.MemoryStream]::new()
    try { $stream.CopyTo($buffer); return ,$buffer.ToArray() }
    finally { $buffer.Dispose(); $stream.Dispose() }
}

function Get-AssetEntryHash($Entry) {
    $stream = $Entry.Open()
    try { Get-AssetStreamHash $stream } finally { $stream.Dispose() }
}

function New-AssetAdditionZip($Additions) {
    $memory = [IO.MemoryStream]::new()
    $zip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($addition in $Additions) {
            $entry = $zip.CreateEntry($addition.name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $entry.ExternalAttributes = 0
            $stream = $entry.Open()
            try { $stream.Write($addition.bytes, 0, $addition.bytes.Length) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    try { return ,$memory.ToArray() } finally { $memory.Dispose() }
}

function New-AssetAppendedZip([string]$Parent, [string]$Destination, $Additions) {
    # Keep the ENTIRE parent archive as an unchanged prefix, including all local
    # headers, compressed data, descriptors, extras, comments, and its old EOCD.
    # Append new entries and a new directory containing exact old directory records.
    $old = Get-AssetZipLayout $Parent
    $newBytes = New-AssetAdditionZip $Additions
    $end = $newBytes.Length - 22
    Assert-Asset ([BitConverter]::ToUInt32($newBytes, $end) -eq 0x06054b50) 'Unexpected addition ZIP end record.'
    $newCount = [BitConverter]::ToUInt16($newBytes, $end + 10)
    $newOffset = [BitConverter]::ToUInt32($newBytes, $end + 16)
    $newSize = [BitConverter]::ToUInt32($newBytes, $end + 12)
    Assert-Asset ($newCount -eq $Additions.Count -and $old.count + $newCount -lt 65535) 'Invalid addition count.'
    $newCentral = [byte[]]::new($newSize)
    [Array]::Copy($newBytes, [int]$newOffset, $newCentral, 0, [int]$newSize)
    $position = 0
    for ($i = 0; $i -lt $newCount; $i++) {
        $localOffset = [BitConverter]::ToUInt32($newCentral, $position + 42)
        Assert-Asset ($old.length + $localOffset -lt [uint32]::MaxValue) 'ZIP64 output is forbidden.'
        [Array]::Copy([BitConverter]::GetBytes([uint32]($old.length + $localOffset)), 0, $newCentral, $position + 42, 4)
        $position += 46 + [BitConverter]::ToUInt16($newCentral, $position + 28) + [BitConverter]::ToUInt16($newCentral, $position + 30) + [BitConverter]::ToUInt16($newCentral, $position + 32)
    }
    $directoryOffset = $old.length + $newOffset
    $directorySize = $old.centralBytes.Length + $newCentral.Length
    Assert-Asset ($directoryOffset + $directorySize + 22 + $old.comment.Length -lt [uint32]::MaxValue) 'ZIP64 output is forbidden.'
    $input = [IO.File]::OpenRead($Parent)
    $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $input.CopyTo($output)
        $output.Write($newBytes, 0, [int]$newOffset)
        $output.Write($old.centralBytes, 0, $old.centralBytes.Length)
        $output.Write($newCentral, 0, $newCentral.Length)
        $writer = [IO.BinaryWriter]::new($output, [Text.Encoding]::UTF8, $true)
        try {
            $writer.Write([uint32]0x06054b50); $writer.Write([uint16]0); $writer.Write([uint16]0)
            $writer.Write([uint16]($old.count + $newCount)); $writer.Write([uint16]($old.count + $newCount))
            $writer.Write([uint32]$directorySize); $writer.Write([uint32]$directoryOffset)
            $writer.Write([uint16]$old.comment.Length); $writer.Write($old.comment)
            $writer.Flush()
        } finally { $writer.Dispose() }
        $output.Flush($true)
    } finally { $input.Dispose(); $output.Dispose() }
}

function Get-AssetPrefixHash([string]$Path, [long]$Length) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $buffer = [byte[]]::new(1048576)
    try {
        for ($remaining = $Length; $remaining -gt 0; $remaining -= $read) {
            $read = $stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
            Assert-Asset ($read -gt 0) 'Truncated preserved ZIP prefix.'
            $hash.AppendData($buffer, 0, $read)
        }
        [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    } finally { $hash.Dispose(); $stream.Dispose() }
}

function Write-AssetNewText([string]$Path, [string]$Text) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function ConvertTo-AssetJson($Value) { ConvertTo-Json -InputObject $Value -Depth 60 -Compress }

function Read-AssetInputs([string]$WorkRoot) {
    $workspace = [IO.Path]::GetDirectoryName($WorkRoot)
    $source = Join-Path $WorkRoot 'Assets/Source'
    $lockPath = Join-Path $source 'AssetInputs.lock.json'
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    Assert-Asset ($lock.schemaVersion -eq 1 -and $lock.packages.Count -eq 5 -and $lock.bindings.Count -eq 8) 'Unexpected asset input lock.'
    $catalogPath = Resolve-AssetInput $workspace $lock.sourceCatalogPath
    Assert-Asset ((Get-AssetHash $catalogPath) -ceq $lock.sourceCatalogSha256) 'Pinned RC25 source catalog changed.'
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    Assert-Asset ($catalog.version -ceq 'kesas-rc25') 'Expected the RC25 staged catalog.'
    foreach ($package in $lock.packages) {
        $parent = Resolve-AssetInput $workspace $package.archivePath
        Assert-Asset ([IO.FileInfo]::new($parent).Length -eq $package.sizeBytes -and (Get-AssetHash $parent) -ceq $package.sha256) "Pinned parent changed: $($package.root)"
        [void](Get-AssetZipLayout $parent)
        foreach ($profile in @('full', 'potato')) {
            $matches = @($catalog.$profile.managedPackages | Where-Object id -CEQ $package.id)
            Assert-Asset ($matches.Count -eq 1) "Expected exactly one $profile declaration for $($package.id)"
            $decl = $matches[0]
            Assert-Asset ($decl.sha256 -ceq $package.sha256 -and $decl.sizeBytes -eq $package.sizeBytes -and $decl.stripPrefix -ceq $package.root -and $decl.destination -ceq 'GameData') "Profile parent mismatch: $profile/$($package.id)"
        }
        $full = @($catalog.full.managedPackages | Where-Object id -CEQ $package.id)[0]
        $potato = @($catalog.potato.managedPackages | Where-Object id -CEQ $package.id)[0]
        Assert-Asset ((ConvertTo-AssetJson $full) -ceq (ConvertTo-AssetJson $potato)) "Profiles do not share an identical parent: $($package.id)"
    }
    [pscustomobject]@{ workspace = $workspace; source = $source; lock = $lock; lockPath = $lockPath; catalog = $catalog; catalogPath = $catalogPath }
}
