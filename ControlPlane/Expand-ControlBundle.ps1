#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$ExpectedSize
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$limit = 128MB
$archiveStream = $null
$zip = $null
$document = $null
$scratch = $null
$written = [Collections.Generic.List[string]]::new()
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

function Assert-LocalPath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -match '[\x00-\x1f]' -or
        $Value.StartsWith('\\') -or $Value.StartsWith('//') -or
        -not [IO.Path]::IsPathFullyQualified($Value) -or
        @($Value -split '[\\/]' | Where-Object { $_ -in @('.', '..') }).Count) { throw 'Unsafe path' }
    if ($IsWindows -and ($Value -notmatch '^[a-zA-Z]:[\\/]' -or $Value.Substring(2).Contains(':'))) { throw 'Unsafe path' }
    $full = [IO.Path]::GetFullPath($Value)
    $root = [IO.Path]::GetPathRoot($full)
    if ([IO.DriveInfo]::new($root).DriveType -in @([IO.DriveType]::Network, [IO.DriveType]::Unknown, [IO.DriveType]::NoRootDirectory)) { throw 'Nonlocal path' }
    $cursor = $root
    $parts = @($full.Substring($root.Length) -split '[\\/]' | Where-Object { $_ })
    foreach ($part in @('') + $parts) {
        if ($part) { $cursor = [IO.Path]::Combine($cursor, $part) }
        try { $attributes = [IO.File]::GetAttributes($cursor) }
        catch {
            $cause = $_.Exception.InnerException
            if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { break }
            throw 'Inaccessible path'
        }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse path' }
        if ($cursor -cne $full -and -not ($attributes -band [IO.FileAttributes]::Directory)) { throw 'Nondirectory ancestor' }
    }
    return $full
}
function Assert-EmptyDestination([string]$Value) {
    $null = Assert-LocalPath $Value
    if ([IO.File]::Exists($Value)) { throw 'Destination is a file' }
    if ([IO.Directory]::Exists($Value)) {
        $items = [IO.Directory]::EnumerateFileSystemEntries($Value).GetEnumerator()
        try { if ($items.MoveNext()) { throw 'Destination is not empty' } } finally { $items.Dispose() }
    }
}
function Test-ControlPath([string]$Name) {
    if ($Name -cmatch '[^\x20-\x7e]' -or $Name.Contains('\')) { return $false }
    foreach ($part in $Name.Split('/')) {
        if ($part -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)' -or $part.EndsWith('.') -or $part.EndsWith(' ')) { return $false }
    }
    return $Name -cmatch '\A(?:ControlPlane/[a-z][a-z0-9-]*\.mjs|ControlPlane/(?:Read-ClientInventory|Invoke-NativePublisher)\.ps1|APFDiscordBot/src/[a-z][a-z0-9-]*\.js|APFDiscordBot/package(?:-lock)?\.json|APFDiscordBot/scripts/(?:Verify-KesasRelease|Stop-KesasGracefully|Stop-APFLmpGracefully)\.ps1|KESAS-VPS-Server/(?:Publish-Release|Update.Common|GitHub.Publish|Export-Client|Start-Server|Stop-Server)\.ps1)\z'
}
function Assert-JsonUnique([System.Text.Json.JsonElement]$Value) {
    if ($Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Value.EnumerateObject()) {
            if (-not $seen.Add($property.Name)) { throw 'Duplicate JSON property' }
            Assert-JsonUnique $property.Value
        }
    } elseif ($Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Value.EnumerateArray()) { Assert-JsonUnique $item }
    }
}
function Assert-Fields([System.Text.Json.JsonElement]$Value, [string[]]$Fields) {
    if ($Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Expected object' }
    $properties = @($Value.EnumerateObject())
    if ($properties.Count -ne $Fields.Count) { throw 'Unexpected fields' }
    foreach ($property in $properties) { if ($Fields -cnotcontains $property.Name) { throw 'Unexpected field' } }
}
function Get-JsonString([System.Text.Json.JsonElement]$Value) {
    if ($Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw 'Expected string' }
    return $Value.GetString()
}
function Get-JsonInteger([System.Text.Json.JsonElement]$Value) {
    if ($Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number) { throw 'Expected number' }
    $number = $Value.GetDouble()
    if (-not [double]::IsFinite($number) -or $number -lt 0 -or $number -gt 9007199254740991 -or [Math]::Truncate($number) -ne $number) { throw 'Expected safe integer' }
    return [long]$number
}

try {
    if ($ExpectedVersion -cnotmatch '\Acontrol-[1-9][0-9]{0,8}\z' -or
        $ExpectedSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $ExpectedSize -cnotmatch '\A[1-9][0-9]*\z') { throw 'Invalid expectation' }
    $expectedLength = [long]::Parse($ExpectedSize, [Globalization.CultureInfo]::InvariantCulture)
    if ($expectedLength -gt $limit) { throw 'Archive too large' }
    $archivePath = Assert-LocalPath $Archive
    $destinationPath = Assert-LocalPath $Destination
    Assert-EmptyDestination $destinationPath
    $archiveStream = [IO.FileStream]::new($archivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ($archiveStream.Length -ne $expectedLength) { throw 'Archive size mismatch' }
    $bytes = [byte[]]::new([int]$expectedLength)
    $offset = 0
    while ($offset -lt $bytes.Length) {
        $read = $archiveStream.Read($bytes, $offset, $bytes.Length - $offset)
        if (-not $read) { throw 'Truncated archive' }
        $offset += $read
    }
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $ExpectedSha256) { throw 'Archive hash mismatch' }

    # Validate both ZIP directories and local records, including metadata that
    # ZipArchive does not expose. Only ordinary stored/deflated files are accepted.
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
public sealed class ControlZipRecord {
    public string Name; public int Offset, Size, Packed; public uint Crc; public ushort Flags, Method;
}
public static class ControlZipCheck {
    static void Need(bool ok) { if (!ok) throw new InvalidDataException("Invalid control ZIP"); }
    static void Bounds(byte[] b, long p, long n) { Need(p >= 0 && n >= 0 && p + n <= b.Length); }
    static ushort U16(byte[] b, int p) { Bounds(b,p,2); return (ushort)(b[p] | b[p+1]<<8); }
    static uint U32(byte[] b, int p) { Bounds(b,p,4); return (uint)(b[p] | b[p+1]<<8 | b[p+2]<<16 | b[p+3]<<24); }
    static int I32(byte[] b, int p) { return checked((int)U32(b,p)); }
    static void Extras(byte[] b, int p, int length) {
        Bounds(b,p,length); int end=p+length; var seen=new HashSet<ushort>();
        while(p<end) {
            Need(p+4<=end); ushort id=U16(b,p); int size=U16(b,p+2); p+=4;
            Need(p+size<=end && seen.Add(id));
            // Timestamp and UID/GID fields cannot encode links. Unknown fields,
            // Unix link records, ZIP64, and NTFS reparse extensions fail closed.
            Need(id==0x5455 || id==0x7875 || id==0x000a);
            if(id==0x000a) {
                Need(size>=4); int q=p+4;
                while(q<p+size) { Need(q+4<=p+size && U16(b,q)==1 && U16(b,q+2)==24); q+=28; }
                Need(q==p+size);
            }
            p+=size;
        }
        Need(p==end);
    }
    public static ControlZipRecord[] Read(byte[] b) {
        Need(b.Length>=22); int end=-1;
        for(int p=b.Length-22;p>=Math.Max(0,b.Length-65557);p--) {
            if(U32(b,p)==0x06054b50 && p+22+U16(b,p+20)==b.Length) { end=p; break; }
        }
        Need(end>=0 && U16(b,end+4)==0 && U16(b,end+6)==0);
        int count=U16(b,end+10), directory=I32(b,end+16), sizeDirectory=I32(b,end+12);
        Need(count>=1 && count<=201 && U16(b,end+8)==count && (long)directory+sizeDirectory==end);
        int cursor=directory; var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows=new List<ControlZipRecord>(); long expanded=0;
        for(int i=0;i<count;i++) {
            Bounds(b,cursor,46); Need(U32(b,cursor)==0x02014b50 && U16(b,cursor+6)<=20 && U16(b,cursor+34)==0);
            int nameLength=U16(b,cursor+28), extraLength=U16(b,cursor+30), commentLength=U16(b,cursor+32);
            int next=checked(cursor+46+nameLength+extraLength+commentLength); Need(next<=end && nameLength>0);
            for(int j=0;j<nameLength;j++) Need(b[cursor+46+j]>=32 && b[cursor+46+j]<=126);
            string name=Encoding.ASCII.GetString(b,cursor+46,nameLength); Need(names.Add(name));
            uint attributes=U32(b,cursor+38), kind=(attributes>>16)&0xf000;
            Need((kind==0 || kind==0x8000) && (attributes&0x450)==0 && !name.EndsWith("/"));
            var row=new ControlZipRecord { Name=name, Offset=I32(b,cursor+42), Size=I32(b,cursor+24), Packed=I32(b,cursor+20), Crc=U32(b,cursor+16), Flags=U16(b,cursor+8), Method=U16(b,cursor+10) };
            Need((row.Flags & ~0x080e)==0 && (row.Method==0 || row.Method==8));
            Need(row.Size<=134217728 && row.Packed<=134217728); expanded+=row.Size; Need(expanded<=134217728);
            Extras(b,cursor+46+nameLength,extraLength); rows.Add(row); cursor=next;
        }
        Need(cursor==end); rows.Sort((a,c)=>a.Offset.CompareTo(c.Offset)); cursor=0;
        foreach(var row in rows) {
            Need(row.Offset==cursor); Bounds(b,cursor,30); Need(U32(b,cursor)==0x04034b50 && U16(b,cursor+4)<=20);
            Need(U16(b,cursor+6)==row.Flags && U16(b,cursor+8)==row.Method);
            int nameLength=U16(b,cursor+26), extraLength=U16(b,cursor+28);
            Bounds(b,cursor+30,(long)nameLength+extraLength);
            Need(nameLength==row.Name.Length);
            for(int j=0;j<nameLength;j++) Need(b[cursor+30+j]==row.Name[j]);
            Extras(b,cursor+30+nameLength,extraLength);
            bool descriptor=(row.Flags&8)!=0;
            Need((descriptor && U32(b,cursor+14)==0) || U32(b,cursor+14)==row.Crc);
            Need((descriptor && U32(b,cursor+18)==0) || U32(b,cursor+18)==row.Packed);
            Need((descriptor && U32(b,cursor+22)==0) || U32(b,cursor+22)==row.Size);
            long next=(long)cursor+30+nameLength+extraLength+row.Packed; Need(next<=directory); cursor=(int)next;
            if(descriptor) {
                if(U32(b,cursor)==0x08074b50) cursor+=4;
                Need(U32(b,cursor)==row.Crc && U32(b,cursor+4)==row.Packed && U32(b,cursor+8)==row.Size); cursor+=12;
            }
            Need(cursor<=directory);
        }
        Need(cursor==directory); return rows.ToArray();
    }
    public static uint Crc32(byte[] bytes) {
        uint crc=0xffffffff; foreach(byte value in bytes) { crc^=value; for(int k=0;k<8;k++) crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u); } return ~crc;
    }
}
'@
    $records = [ControlZipCheck]::Read($bytes)
    $memory = [IO.MemoryStream]::new($bytes, $false)
    $zip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read, $false)
    if ($zip.Entries.Count -ne $records.Count) { throw 'ZIP inventory mismatch' }
    $payload = [Collections.Generic.Dictionary[string,byte[]]]::new([StringComparer]::Ordinal)
    foreach ($record in $records) {
        if ($record.Name -cne 'CONTROL.json' -and -not (Test-ControlPath $record.Name)) { throw 'Unlisted path' }
        $entry = $zip.GetEntry($record.Name)
        if ($null -eq $entry -or $entry.Length -ne $record.Size -or $entry.CompressedLength -ne $record.Packed) { throw 'ZIP metadata mismatch' }
        $content = [byte[]]::new($record.Size)
        $stream = $entry.Open()
        try {
            $offset = 0
            while ($offset -lt $content.Length) {
                $read = $stream.Read($content, $offset, $content.Length - $offset)
                if (-not $read) { throw 'Truncated entry' }
                $offset += $read
            }
            if ($stream.ReadByte() -ne -1) { throw 'Oversized entry' }
        } finally { $stream.Dispose() }
        if ([ControlZipCheck]::Crc32($content) -ne $record.Crc) { throw 'Entry CRC mismatch' }
        $payload.Add($record.Name, $content)
    }
    if (-not $payload.ContainsKey('CONTROL.json')) { throw 'Missing manifest' }
    $json = [Text.UTF8Encoding]::new($false, $true).GetString($payload['CONTROL.json'])
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 16
    $document = [System.Text.Json.JsonDocument]::Parse([string]$json, $options)
    $manifest = $document.RootElement
    Assert-JsonUnique $manifest
    Assert-Fields $manifest @('schemaVersion','transactionVersion','version','runtime','capabilities','files')
    if ((Get-JsonInteger $manifest.GetProperty('schemaVersion')) -ne 1 -or
        (Get-JsonInteger $manifest.GetProperty('transactionVersion')) -ne 1 -or
        (Get-JsonString $manifest.GetProperty('version')) -cne $ExpectedVersion) { throw 'Manifest version mismatch' }
    $runtime = $manifest.GetProperty('runtime')
    Assert-Fields $runtime @('nodeMajor','powerShellMajor','dotnetMajor')
    if ((Get-JsonInteger $runtime.GetProperty('nodeMajor')) -ne 24 -or
        (Get-JsonInteger $runtime.GetProperty('powerShellMajor')) -ne 7 -or
        (Get-JsonInteger $runtime.GetProperty('dotnetMajor')) -ne 8) { throw 'Unsupported runtime' }
    $capabilities = @('application-v1','media-payload-v1','control-plane-v1','host-report-v1','guard-15m-empty-v1','atomic-control-rollback-v1','native-tools-v1','versioned-broker-v1','independent-report-v1','unattended-host-v1','media-reference-v1')
    $caps = $manifest.GetProperty('capabilities')
    if ($caps.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $caps.GetArrayLength() -lt 7 -or $caps.GetArrayLength() -gt 64) { throw 'Unsupported capabilities' }
    $seenCaps = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($cap in $caps.EnumerateArray()) { $name = Get-JsonString $cap; if ($name -cnotmatch '^[a-z][a-z0-9-]{0,79}$' -or -not $seenCaps.Add($name)) { throw 'Unsupported capability' } }
    foreach($name in $capabilities[0..6]){if(-not $seenCaps.Contains($name)){throw 'Missing baseline capability'}}
    # Keep this explicit list synchronized with REQUIRED_CONTROL_FILES in contract.mjs.
    $required = @(
        'ControlPlane/worker.mjs','ControlPlane/engine.mjs','ControlPlane/contract.mjs','ControlPlane/github.mjs','ControlPlane/public-status.mjs',
        'ControlPlane/Read-ClientInventory.ps1',
        'APFDiscordBot/package.json','APFDiscordBot/package-lock.json',
        'APFDiscordBot/src/index.js','APFDiscordBot/src/config.js','APFDiscordBot/src/update-manager.js','APFDiscordBot/src/github-deployment.js','APFDiscordBot/src/lmp.js',
        'APFDiscordBot/src/native-broker.js',
        'APFDiscordBot/scripts/Verify-KesasRelease.ps1',
        'KESAS-VPS-Server/Publish-Release.ps1','KESAS-VPS-Server/Update.Common.ps1','KESAS-VPS-Server/GitHub.Publish.ps1',
        'KESAS-VPS-Server/Export-Client.ps1','KESAS-VPS-Server/Start-Server.ps1','KESAS-VPS-Server/Stop-Server.ps1'
    )
    $files = $manifest.GetProperty('files')
    if ($files.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $files.GetArrayLength() -lt $required.Count -or $files.GetArrayLength() -gt 200 -or $payload.Count -ne $files.GetArrayLength() + 1) { throw 'Invalid inventory' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = 0L
    foreach ($file in $files.EnumerateArray()) {
        Assert-Fields $file @('path','sha256','sizeBytes')
        $name = Get-JsonString $file.GetProperty('path')
        $hash = Get-JsonString $file.GetProperty('sha256')
        $size = Get-JsonInteger $file.GetProperty('sizeBytes')
        if (-not (Test-ControlPath $name) -or -not $seen.Add($name) -or -not $payload.ContainsKey($name) -or $hash -cnotmatch '\A[a-f0-9]{64}\z' -or $size -ne $payload[$name].Length) { throw 'Invalid file declaration' }
        $total += $size
        if ($total -gt $limit -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload[$name])).ToLowerInvariant() -cne $hash) { throw 'File digest mismatch' }
    }
    foreach ($name in $required) { if (-not $seen.Contains($name)) { throw 'Missing required file' } }
    # Convert only after JsonDocument has rejected duplicate properties and types.
    $converted = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 16 -NoEnumerate
    if ($converted['version'] -cne $ExpectedVersion) { throw 'JSON conversion mismatch' }

    # No destination writes occur until every entry and the complete manifest pass.
    Assert-EmptyDestination $destinationPath
    $parent = [IO.Path]::GetDirectoryName($destinationPath)
    $null = Assert-LocalPath $parent
    $null = [IO.Directory]::CreateDirectory($parent)
    $null = Assert-LocalPath $parent
    $scratch = [IO.Path]::Combine($parent, '.control-extract-' + [Guid]::NewGuid().ToString('N'))
    $null = Assert-LocalPath $scratch
    if ([IO.Directory]::Exists($scratch) -or [IO.File]::Exists($scratch)) { throw 'Staging conflict' }
    $null = [IO.Directory]::CreateDirectory($scratch)
    $null = $directories.Add($scratch)
    foreach ($name in $payload.Keys) {
        $target = [IO.Path]::Combine($scratch, $name.Replace('/', [IO.Path]::DirectorySeparatorChar))
        $directory = [IO.Path]::GetDirectoryName($target)
        $null = Assert-LocalPath $directory
        $pending = [Collections.Generic.List[string]]::new()
        for ($cursor = $directory; -not [IO.Directory]::Exists($cursor); $cursor = [IO.Path]::GetDirectoryName($cursor)) { $pending.Add($cursor) }
        $null = [IO.Directory]::CreateDirectory($directory)
        foreach ($created in $pending) { $null = $directories.Add($created) }
        $null = Assert-LocalPath $target
        $output = [IO.FileStream]::new($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $written.Add($target)
        try { $output.Write($payload[$name], 0, $payload[$name].Length); $output.Flush($true) } finally { $output.Dispose() }
    }
    Assert-EmptyDestination $destinationPath
    if ([IO.Directory]::Exists($destinationPath)) { [IO.Directory]::Delete($destinationPath, $false) }
    $null = Assert-LocalPath $scratch
    [IO.Directory]::Move($scratch, $destinationPath)
    $scratch = $null
    [Console]::Out.WriteLine('verified ' + $ExpectedVersion)
} catch {
    $nativeCode=$_.Exception.HResult -band 0xffff
    if($_.Exception -is [IO.IOException] -and $nativeCode -in @(32,33,170)){
        [Console]::Error.WriteLine('CONTROL_EXTRACT_IO_BUSY')
        exit 75
    }
    [Console]::Error.WriteLine('Control bundle rejected.')
    exit 1
} finally {
    if ($null -ne $document) { $document.Dispose() }
    if ($null -ne $zip) { $zip.Dispose() }
    if ($null -ne $archiveStream) { $archiveStream.Dispose() }
    if ($null -ne $scratch) {
        # Delete only known files and empty directories owned by this extraction.
        foreach ($file in $written) { try { $null = Assert-LocalPath $file; [IO.File]::Delete($file) } catch {} }
        foreach ($directory in @($directories | Sort-Object Length -Descending)) { try { $null = Assert-LocalPath $directory; [IO.Directory]::Delete($directory, $false) } catch {} }
    }
}
