[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$WorkspaceRoot,
    [switch]$VerifyOnly,
    [switch]$Refresh,
    [switch]$RefreshMetadata,
    [switch]$FinalizeExisting,
    [switch]$ResumeIncomplete,
    [string[]]$RefreshSource = @(),
    [string]$LgplLicensePath = 'C:\Program Files\Git\mingw64\share\licenses\xz\COPYING.LGPLv2.1'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Affect only this export process, never another process or a running service.
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$workspace = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\','/')
$destination = Join-Path $workspace 'Aetherfront-GitHub-Source'
$utf8 = [Text.UTF8Encoding]::new($false)
$policyPath = Join-Path $destination 'export-policy.json'
if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw 'Missing reviewed staging export-policy.json.' }

function Assert-NoLink([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not exportable.' }
}
Assert-NoLink $workspace
Assert-NoLink $destination
$script:checkedSourcePaths=@{}
function Resolve-Source([string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)' -or $Relative -match '(?i)tsclient') { throw 'Unsafe source selection.' }
    $full = [IO.Path]::GetFullPath((Join-Path $workspace $Relative))
    if (-not $full.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Source escapes workspace.' }
    $cursor = $workspace
    foreach ($part in $Relative.Split([char[]]'\/',[StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $part
        if (-not $script:checkedSourcePaths.ContainsKey($cursor) -and (Test-Path -LiteralPath $cursor)) { Assert-NoLink $cursor; $script:checkedSourcePaths[$cursor]=$true }
    }
    return $full
}
function Hash([string]$Path) {
    $stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create()
    try{[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}
}
function Write-Json([string]$Path,$Value) { [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 30) + "`n",$utf8) }
function Total-Bytes($Items){[long]$total=0;foreach($item in $Items){$total += [long]$item.bytes};return $total}
function Walk([string]$Directory,[switch]$All) {
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force | Sort-Object Name) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { if ($All) { throw 'Staging contains a reparse point; verification refused.' }; $script:excludedLinks++; continue }
        if ($item.PSIsContainer) {
            if (-not $All -and $item.Name -match $policy.deniedDirectoryPattern) { $script:excludedDirectories++; continue }
            Walk $item.FullName -All:$All
        } else { $item }
    }
}
function Scan([string]$Root) {
    $findings = [Collections.Generic.List[object]]::new()
    $reviewed = [Collections.Generic.List[object]]::new()
    $cachedRegex=@{};$ruleMilliseconds=@{};$scanTimer=[Diagnostics.Stopwatch]::StartNew()
    $scanned = 0
    foreach ($file in Walk $Root -All) {
        $relative = $file.FullName.Substring($Root.Length+1).Replace('\','/')
        # The scanner's patterns are not credential material. Scan it normally.
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        if ($bytes.Length -gt 8MB) { $findings.Add([ordered]@{path=$relative;rule='oversize'}); continue }
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text.IndexOf([char]0) -ge 0) { $findings.Add([ordered]@{path=$relative;rule='binary-content'}); continue }
        $scanned++
        $rules = [ordered]@{
            'private-key' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----|<RSAKeyValue>\s*<Modulus>[^<]{1,8192}</Modulus>.{0,16384}?<D>[^<]{1,8192}</D>'
            'github-token' = '\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b'
            'cloud-key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
            'discord-token' = '(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{23,28}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,110}(?![A-Za-z0-9_-])'
            'credential-url' = 'https?://[^\s/"''<>]+:[^\s/@"''<>]+@'
            'credential-literal' = '(?i)(?:["'']?(?:password|passwd|secret|api[_-]?key|access[_-]?token|upload[_-]?token|diagnostics[_-]?token|discord[_-]?token|bot[_-]?token|token)["'']?\s*[:=]\s*["''])([^"''\r\n]{8,})["'']'
            'user-home' = '(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public[\\/])[^\\/\s"'']+'
            'private-host' = '(?i)(?:\\\\tsclient\\|[A-Z]:\\Users\\[^\\\r\n]+\\(?:\.ssh|\.aws|\.azure)\\)'
        }
        foreach ($rule in $rules.GetEnumerator()) {
            if(-not $cachedRegex.ContainsKey($rule.Key)){$cachedRegex[$rule.Key]=[regex]::new($rule.Value,[Text.RegularExpressions.RegexOptions]::Singleline,[TimeSpan]::FromSeconds(5));$ruleMilliseconds[$rule.Key]=0.0}
            $ruleTimer=[Diagnostics.Stopwatch]::StartNew()
            try{$ruleMatches=@($cachedRegex[$rule.Key].Matches($text))}catch [Text.RegularExpressions.RegexMatchTimeoutException]{$findings.Add([ordered]@{path=$relative;rule=($rule.Key+'-timeout')});continue}finally{$ruleMilliseconds[$rule.Key]+=$ruleTimer.Elapsed.TotalMilliseconds}
            foreach ($match in $ruleMatches) {
                if ($rule.Key -eq 'credential-literal') {
                    $value = $match.Groups[1].Value
                    if ($value -match '^(?:\$|<|\{|YOUR_|REPLACE_|PASTE[-_ ]|EXAMPLE_|example|placeholder|test|dummy|fixture|fake|redacted|REDACTED|change-me|changeme)' -or $value -match '^(?:password|aetherfront-dev|not-a-real-token)$') { continue }
                    if($relative -eq 'scripts/Export-GitHubSource.ps1' -and $value -in $rules.Values){continue}
                }
                $sha=[Security.Cryptography.SHA256]::Create();try{$matchHash=[BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($match.Value))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
                $exception=@($policy.reviewedMatches|Where-Object {$_.path -eq $relative -and $_.rule -eq $rule.Key -and $_.matchSha256 -eq $matchHash})
                if($exception.Count){$reviewed.Add([ordered]@{path=$relative;rule=$rule.Key;matchSha256=$matchHash;reason=$exception[0].reason});continue}
                $finding = [ordered]@{path=$relative;rule=$rule.Key}
                if (-not ($findings | Where-Object { $_.path -eq $relative -and $_.rule -eq $rule.Key })) { $findings.Add($finding) }
            }
        }
    }
    return [ordered]@{scannedUtc=[DateTime]::UtcNow.ToString('o');filesScanned=$scanned;elapsedSeconds=$scanTimer.Elapsed.TotalSeconds;ruleMilliseconds=$ruleMilliseconds;regexTimeoutSecondsPerFileRule=5;findingCount=$findings.Count;findings=@($findings.ToArray());reviewedSyntheticMatches=@($reviewed.ToArray());contentOrMatchedValuesPrinted=$false;scope='Heuristic static text scan; not a guarantee of absence of secrets. A regex timeout is a failing finding, never an omitted check.'}
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$script:excludedDirectories = 0; $script:excludedLinks = 0
$manifestPath = Join-Path $destination 'SOURCE-MANIFEST.json'
if($Refresh){$ResumeIncomplete=$true}
if($FinalizeExisting){
    if(Test-Path -LiteralPath $manifestPath){throw 'A manifest already exists; use targeted -RefreshSource or -RefreshMetadata instead.'}
    if(-not $ResumeIncomplete -or $Refresh -or $VerifyOnly -or $RefreshMetadata){throw 'Finalizing existing inspected copies requires -ResumeIncomplete only, optionally with approved -RefreshSource paths.'}
    $metadata=@($policy.metadataFiles)+@('LICENSE','licenses/LGPL-2.1.txt','scripts/Export-GitHubSource.ps1','scripts/Test-SourceStage.ps1','BINARY-PREREQUISITES.json','DEPENDENCIES.json','PRIVACY-SCAN.json')
    $records=[Collections.Generic.List[object]]::new();$present=@{};$refreshed=@{}
    foreach($file in Walk $destination -All){
        $relative=$file.FullName.Substring($destination.Length+1).Replace('\','/');$present[$relative]=$true
        if($relative -in $metadata){continue}
        if($relative -match $policy.deniedFilePattern){throw 'Excluded source file remains in incomplete staging; inspect before finalization.'}
        $explicit=@($policy.explicitFiles|Where-Object {$_.source -eq $relative});$role=$null
        if($explicit.Count){$role=$explicit[0].role}else{
            foreach($group in $policy.groups){foreach($directory in $group.directories){if($relative.StartsWith($directory+'/',[StringComparison]::OrdinalIgnoreCase) -and $file.Extension.ToLowerInvariant() -in $group.extensions){$inside=$relative.Substring($directory.Length+1);$parts=$inside.Split('/');$blocked=@($parts|Select-Object -SkipLast 1|Where-Object {$_ -match $policy.deniedDirectoryPattern});if(-not $blocked.Count){$role=$group.role}}}}
        }
        if(-not $role){throw 'Existing file is outside the reviewed source/metadata allowlist; nothing adopted for it.'}
        $source=Resolve-Source $relative;$sourceHash=Hash $source;$text=[IO.File]::ReadAllText($source);$updatedText=$text.Replace($workspace,'.').Replace($workspace.Replace('\','/'),'.').Replace($workspace.Replace('\','\\'),'.');$changes=@()
        $expectedHash=$sourceHash
        if($updatedText -ne $text){$changes=@('absolute workspace root replaced by .');$sha=[Security.Cryptography.SHA256]::Create();try{$expectedHash=[BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($updatedText))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}}
        if((Hash $file.FullName) -ne $expectedHash){if($relative -notin $RefreshSource){throw "Staged/source difference requires an explicit reviewed refresh: $relative"};if($changes.Count){[IO.File]::WriteAllText($file.FullName,$updatedText,$utf8)}else{[IO.File]::Copy($source,$file.FullName,$true)};$refreshed[$relative]=$true}
        if((Hash $source) -ne $sourceHash){throw 'Original source changed during existing-copy finalization; review required.'}
        $records.Add([ordered]@{path=$relative;source=$relative;role=$role;bytes=([IO.FileInfo]$file.FullName).Length;sha256=(Hash $file.FullName);sourceSha256=$sourceHash;sourceModifiedUtc=[IO.File]::GetLastWriteTimeUtc($source).ToString('o');transformations=$changes})
    }
    foreach($entry in $policy.explicitFiles){if(-not $present.ContainsKey($entry.source)){throw "Required explicitly selected source is absent: $($entry.source)"}}
    foreach($relative in $RefreshSource){if(-not $present.ContainsKey($relative)){throw 'A requested source refresh is absent from inspected staging.'}}
    $scriptTarget=Join-Path $destination 'scripts/Export-GitHubSource.ps1';if([IO.Path]::GetFullPath($PSCommandPath) -ne [IO.Path]::GetFullPath($scriptTarget)){[IO.File]::Copy($PSCommandPath,$scriptTarget,$true)}
    $scan=Scan $destination;Write-Json (Join-Path $destination 'PRIVACY-SCAN.json') $scan;if($scan.findingCount){throw "Final privacy review failed: $($scan.findingCount) redacted findings."}
    foreach($file in Walk $destination -All){$relative=$file.FullName.Substring($destination.Length+1).Replace('\','/');if($relative -in @('SOURCE-MANIFEST.json','PRIVACY-SCAN.json') -or $relative -notin $metadata){continue};$records.Add([ordered]@{path=$relative;source=$null;role='publication metadata, license or export tooling';bytes=$file.Length;sha256=(Hash $file.FullName);sourceSha256=$null;transformations=@()})}
    $manifest=[ordered]@{schemaVersion=1;repository='esotericmartyristaken-bit/AETHERFRONT';createdUtc=[DateTime]::UtcNow.ToString('o');publication='STAGED ONLY; not pushed';sourceBase=[ordered]@{tree='Aetherfront-Source';upstream='LunaMultiplayer';tag='0.29.2';commit='60a2ed5dbfdddc08536a98b999c781e3c12b0649';commitEvidence='Sibling upstream Git metadata and existing handoff; not a commit identity for modified source.'};rebuildStatus='NOT independently rebuildable as shipping RC22 from source export alone; see README.md / SOURCE-PROVENANCE.md';concurrentSourceReviewPending=$true;integrationGates=@('Human review source, privacy and licenses before publication','UnityTlsSmoke intentionally deferred until stable','Refresh source or metadata after further changes');files=@($records.ToArray()|Sort-Object path);totalFileBytes=(Total-Bytes $records);exclusions=@($policy.exclusions);privacy=[ordered]@{findingCount=0;report='PRIVACY-SCAN.json';limitation=$scan.scope}}
    Write-Json $manifestPath $manifest;Write-Output "Finalized inspected source copies: $($records.Count) manifest files; $($refreshed.Count) source files refreshed; $($manifest.totalFileBytes) bytes; privacy findings 0."
    return
}
if((Test-Path -LiteralPath $manifestPath) -and $RefreshSource.Count -and -not $Refresh -and @($RefreshSource|Where-Object {[IO.Path]::GetExtension($_) -in @('.csproj','.props','.targets')}).Count -eq 0){
    if(-not $ResumeIncomplete -or $VerifyOnly -or $RefreshMetadata){throw 'Targeted refresh requires -ResumeIncomplete and cannot be combined with verification/metadata modes.'}
    $old=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    $known=@{};foreach($record in $old.files){$known[$record.path]=$record;$path=Join-Path $destination $record.path;if(-not [IO.File]::Exists($path) -or (Hash $path) -ne $record.sha256){throw 'Existing staged content differs from its manifest; inspect before targeted refresh.'}}
    foreach($relative in $RefreshSource){
        if(-not $known.ContainsKey($relative) -or -not $known[$relative].source -or $known[$relative].source -ne $relative){throw 'Targeted refresh requires an existing manifest-selected source path; use -Refresh for new allowlist additions.'}
        $source=Resolve-Source $relative;$sourceHash=Hash $source;$text=[IO.File]::ReadAllText($source)
        $updatedText=$text.Replace($workspace,'.').Replace($workspace.Replace('\','/'),'.').Replace($workspace.Replace('\','\\'),'.')
        if((Hash $source) -ne $sourceHash){throw 'Source changed during targeted refresh; no copy made for this file.'}
        $target=Join-Path $destination $relative
        if($updatedText -ne $text){[IO.File]::WriteAllText($target,$updatedText,$utf8);$transformations=@('absolute workspace root replaced by .')}else{[IO.File]::Copy($source,$target,$true);$transformations=@()}
        $record=$known[$relative];$record.sha256=Hash $target;$record.sourceSha256=$sourceHash;$record.sourceModifiedUtc=[IO.File]::GetLastWriteTimeUtc($source).ToString('o');$record.bytes=([IO.FileInfo]$target).Length;$record.transformations=$transformations
    }
    $scriptTarget=Join-Path $destination 'scripts/Export-GitHubSource.ps1'
    if([IO.Path]::GetFullPath($PSCommandPath) -ne [IO.Path]::GetFullPath($scriptTarget)){[IO.File]::Copy($PSCommandPath,$scriptTarget,$true)}
    foreach($file in Walk $destination -All){$relative=$file.FullName.Substring($destination.Length+1).Replace('\','/');if(-not $known.ContainsKey($relative) -and $relative -notin $policy.metadataFiles -and $relative -notin @('SOURCE-MANIFEST.json','PRIVACY-SCAN.json')){throw 'Unreviewed staged path appeared; targeted refresh refused.'}}
    $scan=Scan $destination;Write-Json (Join-Path $destination 'PRIVACY-SCAN.json') $scan
    if($scan.findingCount){throw "Targeted source privacy review failed: $($scan.findingCount) redacted findings. No new manifest finalized."}
    $updated=@(foreach($file in Walk $destination -All){$relative=$file.FullName.Substring($destination.Length+1).Replace('\','/');if($relative -in @('SOURCE-MANIFEST.json','PRIVACY-SCAN.json')){continue};if($known.ContainsKey($relative) -and $known[$relative].source){$known[$relative]}else{[ordered]@{path=$relative;source=$null;role='publication metadata, license or export tooling';bytes=$file.Length;sha256=(Hash $file.FullName);sourceSha256=$null;transformations=@()}}})
    $old.files=@($updated|Sort-Object path);$old.totalFileBytes=Total-Bytes $updated;$old.createdUtc=[DateTime]::UtcNow.ToString('o');Write-Json $manifestPath $old
    Write-Output "Targeted source refresh complete: $($RefreshSource.Count) reviewed source paths; $($updated.Count) manifest files; privacy findings 0. No original source changed."
    return
}
if($RefreshMetadata){
    if($Refresh -or $RefreshSource.Count -or $VerifyOnly){throw 'Metadata refresh cannot be combined with other modes.'}
    if(-not(Test-Path -LiteralPath $manifestPath)){throw 'A source manifest is required before metadata-only refresh.'}
    $old=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    $known=@{};foreach($record in $old.files){$known[$record.path]=$record;if($record.source){$path=Join-Path $destination $record.path;if(-not(Test-Path -LiteralPath $path -PathType Leaf) -or (Hash $path) -ne $record.sha256){throw 'Copied source changed; use reviewed source refresh before metadata-only refresh.'}}}
    foreach($file in Walk $destination -All){$relative=$file.FullName.Substring($destination.Length+1).Replace('\','/');if(-not $known.ContainsKey($relative) -and $relative -notin $policy.metadataFiles -and $relative -notin @('SOURCE-MANIFEST.json','PRIVACY-SCAN.json')){throw 'Unreviewed new metadata path; add it to metadataFiles before refreshing.'}}
    $scan=Scan $destination;Write-Json (Join-Path $destination 'PRIVACY-SCAN.json') $scan
    if($scan.findingCount){throw "Metadata privacy review failed: $($scan.findingCount) redacted findings."}
    $updated=@(foreach($file in Walk $destination -All){$relative=$file.FullName.Substring($destination.Length+1).Replace('\','/');if($relative -in @('SOURCE-MANIFEST.json','PRIVACY-SCAN.json')){continue};if($known.ContainsKey($relative) -and $known[$relative].source){$known[$relative]}else{[ordered]@{path=$relative;source=$null;role='publication metadata, license or export tooling';bytes=$file.Length;sha256=(Hash $file.FullName);sourceSha256=$null;transformations=@()}}})
    $old.files=@($updated|Sort-Object path);$old.totalFileBytes=Total-Bytes $updated;$old.createdUtc=[DateTime]::UtcNow.ToString('o');Write-Json $manifestPath $old
    Write-Output "Refreshed metadata manifest: $($updated.Count) files; privacy findings 0; copied source unchanged."
    return
}
if ((Test-Path -LiteralPath $manifestPath) -and -not $RefreshSource.Count -and -not $Refresh) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $mismatches = 0
    $expected = @{}
    foreach ($record in $manifest.files) {
        $expected[$record.path] = $true
        $path = Join-Path $destination $record.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Hash $path) -ne $record.sha256) { $mismatches++ }
    }
    foreach ($file in Walk $destination -All) {
        $relative=$file.FullName.Substring($destination.Length+1).Replace('\','/')
        if ($relative -notin @('SOURCE-MANIFEST.json','PRIVACY-SCAN.json') -and -not $expected.ContainsKey($relative)) { $mismatches++ }
    }
    $scan = Scan $destination
    if ($mismatches -or $scan.findingCount) { throw "Verification failed: $mismatches manifest mismatches; $($scan.findingCount) privacy findings. No values printed; no files changed." }
    Write-Output "Verified existing immutable export: $($manifest.files.Count) manifest files; privacy findings 0. No files changed."
    return
}
if ($VerifyOnly) { throw 'No completed export manifest exists, or refresh conflicts with verification.' }
if ($RefreshSource.Count -and -not $ResumeIncomplete) { throw 'An explicit source refresh requires -ResumeIncomplete.' }
if (($RefreshSource.Count -or $Refresh) -and (Test-Path -LiteralPath $manifestPath)) {
    $previousManifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    foreach($record in $previousManifest.files | Where-Object {$_.source}){
        $path=Join-Path $destination $record.path
        if(-not(Test-Path -LiteralPath $path -PathType Leaf) -or (Hash $path) -ne $record.sha256){throw 'Existing manifest changed; inspect before an explicit refresh.'}
    }
}
$controls = @('README.md','SOURCE-PROVENANCE.md','THIRD-PARTY-NOTICES.md','.gitignore','.gitattributes','export-policy.json','scripts','licenses','EXPORT-REVIEW.md')
if(-not $ResumeIncomplete){foreach ($entry in Get-ChildItem -LiteralPath $destination -Force) { if ($entry.Name -notin $controls) { throw 'Staging contains unrecognized existing content; inspect manually. Nothing overwritten.' } }}

$selected = [Collections.Generic.List[object]]::new()
$destinations = @{}
function Add-Selection([string]$Relative,[string]$Role) {
    $full = Resolve-Source $Relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Required source file missing: $Relative" }
    $relativePath = $Relative.Replace('\','/')
    if ($destinations.ContainsKey($relativePath)) { return }
    $destinations[$relativePath] = $true
    $selected.Add([ordered]@{source=$relativePath;path=$relativePath;role=$Role})
}
foreach ($group in $policy.groups) {
    foreach ($directory in $group.directories) {
        $full = Resolve-Source $directory
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "Required source directory missing: $directory" }
        foreach ($file in Walk $full) {
            if ($file.Extension.ToLowerInvariant() -notin $group.extensions) { continue }
            $relative=$file.FullName.Substring($workspace.Length+1).Replace('\','/')
            if ($relative -match $policy.deniedFilePattern) { continue }
            Add-Selection $relative $group.role
        }
    }
}
foreach ($entry in $policy.explicitFiles) { Add-Selection $entry.source $entry.role }
if($Refresh){$RefreshSource=@($selected|ForEach-Object {$_.path})}
Write-Output "Allowlisted source files: $($selected.Count). Export process priority: BelowNormal."

# Finish validation before creating any copied source. Never overwrite anything.
foreach ($entry in $selected) { if (-not $ResumeIncomplete -and (Test-Path -LiteralPath (Join-Path $destination $entry.path))) { throw 'An export target already exists; inspect manually. Nothing overwritten.' } }
foreach($relative in $RefreshSource){if(-not $destinations.ContainsKey($relative)){throw 'Refresh target is not in the reviewed source allowlist.'}}
if($ResumeIncomplete){
    $metadata=@($policy.metadataFiles)+@('LICENSE','licenses/LGPL-2.1.txt','scripts/Export-GitHubSource.ps1','scripts/Test-SourceStage.ps1','BINARY-PREREQUISITES.json','DEPENDENCIES.json','PRIVACY-SCAN.json','SOURCE-MANIFEST.json')
    foreach($file in Walk $destination -All){
        $relative=$file.FullName.Substring($destination.Length+1).Replace('\','/')
        if(-not $destinations.ContainsKey($relative) -and $relative -notin $metadata){throw 'Incomplete export contains an unrecognized file; nothing overwritten.'}
    }
}
$licenseTarget=Join-Path $destination 'licenses/LGPL-2.1.txt'
if (-not (Test-Path -LiteralPath $licenseTarget)) {
    if (-not (Test-Path -LiteralPath $LgplLicensePath -PathType Leaf)) { throw 'Supply a local verbatim LGPL 2.1 license text with -LgplLicensePath.' }
    Assert-NoLink $LgplLicensePath
    $licenseText=[IO.File]::ReadAllText($LgplLicensePath)
    if ($licenseText -notmatch 'GNU LESSER GENERAL PUBLIC LICENSE' -or $licenseText -notmatch 'Version 2.1, February 1999' -or $licenseText.Length -lt 20000) { throw 'LGPL text validation failed.' }
    $null=[IO.Directory]::CreateDirectory((Split-Path $licenseTarget))
    [IO.File]::Copy($LgplLicensePath,$licenseTarget,$false)
}
$records=[Collections.Generic.List[object]]::new()
foreach ($entry in $selected | Sort-Object path) {
    $source=Resolve-Source $entry.source
    $target=Join-Path $destination $entry.path
    $beforeHash=Hash $source
    $text=[IO.File]::ReadAllText($source)
    $changes=[Collections.Generic.List[string]]::new()
    # Preserve source semantics for scripts run from the exported workspace root.
    $replacement=$text.Replace($workspace,'.').Replace($workspace.Replace('\','/'),'.').Replace($workspace.Replace('\','\\'),'.')
    if ($replacement -ne $text) { $changes.Add('absolute workspace root replaced by .'); $text=$replacement }
    $null=[IO.Directory]::CreateDirectory((Split-Path $target))
    $expectedHash=$beforeHash
    if($changes.Count){$sha=[Security.Cryptography.SHA256]::Create();try{$expectedHash=[BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($text))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}}
    $exists=Test-Path -LiteralPath $target
    $mustWrite=-not $exists
    if($exists -and (Hash $target) -ne $expectedHash){
        if($entry.path -notin $RefreshSource){throw "Existing staged source differs: $($entry.path). Review it and explicitly name an approved -RefreshSource; nothing overwritten for this file."}
        $changes.Add('explicitly refreshed after source review')
        $mustWrite=$true
    }
    if($mustWrite){if ($changes.Count) { [IO.File]::WriteAllText($target,$text,$utf8) } else { [IO.File]::Copy($source,$target,$exists) }}
    if ((Hash $source) -ne $beforeHash) { throw 'Source changed during export; preserve staging and review before retrying.' }
    $item=Get-Item -LiteralPath $target
    $records.Add([ordered]@{path=$entry.path;source=$entry.source;role=$entry.role;bytes=$item.Length;sha256=(Hash $target);sourceSha256=$beforeHash;sourceModifiedUtc=[IO.File]::GetLastWriteTimeUtc($source).ToString('o');transformations=@($changes.ToArray())})
    if($records.Count % 500 -eq 0){Write-Output "Source files verified/copied: $($records.Count)/$($selected.Count)."}
}
$exportScript=Join-Path $destination 'scripts/Export-GitHubSource.ps1'
if (Test-Path -LiteralPath $exportScript) { if(-not $ResumeIncomplete){throw 'Export script target exists; refusing overwrite.'} }
if([IO.Path]::GetFullPath($PSCommandPath) -ne [IO.Path]::GetFullPath($exportScript)){[IO.File]::Copy($PSCommandPath,$exportScript,[bool]$ResumeIncomplete)}
$upstreamLicense=Join-Path $destination 'LICENSE'
if(Test-Path -LiteralPath $upstreamLicense){if((Hash $upstreamLicense) -ne (Hash (Resolve-Source 'Aetherfront-Source/LICENSE'))){throw 'Root license differs; refusing overwrite.'}}else{[IO.File]::Copy((Resolve-Source 'Aetherfront-Source/LICENSE'),$upstreamLicense,$false)}

$inputRecords=@(foreach ($relative in $policy.binaryPrerequisites) {
    $path=Resolve-Source $relative
    if (Test-Path -LiteralPath $path -PathType Leaf) { $item=Get-Item -LiteralPath $path; [ordered]@{path=$relative;presentDuringExport=$true;bytes=$item.Length;sha256=(Hash $path);exported=$false} }
    else { [ordered]@{path=$relative;presentDuringExport=$false;exported=$false} }
})
Write-Json (Join-Path $destination 'BINARY-PREREQUISITES.json') $inputRecords
$packages=@(foreach ($file in Walk $destination -All | Where-Object { $_.Extension -eq '.csproj' }) {
    [xml]$xml=[IO.File]::ReadAllText($file.FullName)
    foreach ($package in $xml.SelectNodes('//*[local-name()="PackageReference"]')) {
        [ordered]@{project=$file.FullName.Substring($destination.Length+1).Replace('\','/');package=$package.GetAttribute('Include');version=$package.GetAttribute('Version')}
    }
})
Write-Json (Join-Path $destination 'DEPENDENCIES.json') $packages
$scan=Scan $destination
Write-Json (Join-Path $destination 'PRIVACY-SCAN.json') $scan
if ($scan.findingCount) { throw "Export requires review: $($scan.findingCount) privacy findings. See redacted PRIVACY-SCAN.json; no matched values printed. No manifest finalized." }
$tracked=@{}
foreach ($record in $records) { $tracked[$record.path]=$true }
foreach ($file in Walk $destination -All | Sort-Object FullName) {
    $relative=$file.FullName.Substring($destination.Length+1).Replace('\','/')
    if ($relative -in @('PRIVACY-SCAN.json','SOURCE-MANIFEST.json') -or $tracked.ContainsKey($relative)) { continue }
    $records.Add([ordered]@{path=$relative;source=$null;role='publication metadata, license or export tooling';bytes=$file.Length;sha256=(Hash $file.FullName);sourceSha256=$null;transformations=@()})
}
$manifest=[ordered]@{
    schemaVersion=1;repository='esotericmartyristaken-bit/AETHERFRONT';createdUtc=[DateTime]::UtcNow.ToString('o');publication='STAGED ONLY; not pushed';
    sourceBase=[ordered]@{tree='Aetherfront-Source';upstream='LunaMultiplayer';tag='0.29.2';commit='60a2ed5dbfdddc08536a98b999c781e3c12b0649';commitEvidence='Sibling LunaMultiplayer-Source git metadata and existing handoff documentation; not a commit identity for the modified export.'};
    rebuildStatus='NOT independently rebuildable as shipping RC22 from this export alone; see README.md';
    concurrentSourceReviewPending=$true;integrationGates=@('Refresh after native/GitHub publisher review completes','Add forthcoming recovery-test source paths to export-policy.json and refresh','Review source drift, privacy and licenses before publication');
    excludedDirectoryCount=$excludedDirectories;excludedReparsePointCount=$excludedLinks;
    files=@($records.ToArray() | Sort-Object path);totalFileBytes=(Total-Bytes $records);
    exclusions=@($policy.exclusions);privacy=[ordered]@{findingCount=0;report='PRIVACY-SCAN.json';limitation=$scan.scope}
}
Write-Json $manifestPath $manifest
Write-Output "Staged $($records.Count) manifest files; $($manifest.totalFileBytes) bytes; privacy findings 0. No git/auth/publication/process operations."
