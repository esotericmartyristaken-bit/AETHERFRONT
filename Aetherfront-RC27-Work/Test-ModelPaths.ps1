#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EngineResultPath,[Parameter(Mandatory)][string]$ClientLogPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=Split-Path $PSScriptRoot
$engine=Get-Content -LiteralPath $EngineResultPath -Raw|ConvertFrom-Json
if(!$engine.completed -or $engine.version -cne 'kesas-rc27' -or (Get-FileHash -LiteralPath $engine.enginePath -Algorithm SHA256).Hash -ine $engine.engineSha256){throw 'Engine evidence mismatch'}
$catalog=Get-Content -LiteralPath "$($engine.baselinePath)/GameData/LunaMultiplayer/Launcher/install-catalog.json" -Raw|ConvertFrom-Json
$roots=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($package in @($catalog.full.managedPackages)+@($catalog.potato.managedPackages)){
    $destination=([string]$package.destination).Replace('\','/').Trim('/')
    if($destination -ieq 'GameData'){$destination=''}elseif($destination.StartsWith('GameData/',[StringComparison]::OrdinalIgnoreCase)){$destination=$destination.Substring(9)}
    $canonical=if($destination){$destination}else{[string]$package.stripPrefix}
    if($roots.ContainsKey($canonical) -and $roots[$canonical] -cne $canonical){throw 'Profiles disagree on canonical root casing'}
    $roots[$canonical]=$canonical
}
$names=@('InterstellarTechnologies','ModularLaunchPads','TundraSpaceCenter','UmbraSpaceIndustries')
$allPackages=@(@($catalog.full.managedPackages)+@($catalog.potato.managedPackages)|Sort-Object sha256 -Unique)
$allPayloadEntries=0
foreach($package in $allPackages){
    $archive=Join-Path "$workspace/KESAS-VPS-Server/Server/Config" ([IO.Path]::GetFileName(([Uri]$package.archiveUrl).AbsolutePath))
    if((Get-Item -LiteralPath $archive).Length -ne $package.sizeBytes){throw 'Archive size differs from its signed declaration'}
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try{foreach($entry in $zip.Entries){
        # The official 4K texture ZIP contains ancestor directory entries such
        # as GameData/. Like the installer, inspect payload files, not folders.
        if([string]::IsNullOrEmpty($entry.Name)){continue}
        $path=$entry.FullName.Replace('\','/')
        if($path -cne $package.stripPrefix -and !$path.StartsWith($package.stripPrefix+'/',[StringComparison]::Ordinal)){
            throw "Archive payload prefix casing differs: $($package.id)/$path"
        }
        $allPayloadEntries++
    }}finally{$zip.Dispose()}
}
$models=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$archiveEntries=0
foreach($name in $names){
    $package=@($catalog.full.managedPackages|Where-Object stripPrefix -CEQ $name)
    if($package.Count -ne 1){throw 'Affected package identity is missing/ambiguous'}
    $archive=Join-Path "$workspace/KESAS-VPS-Server/Server/Config" ([IO.Path]::GetFileName(([Uri]$package[0].archiveUrl).AbsolutePath))
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $package[0].sha256 -or (Get-Item -LiteralPath $archive).Length -ne $package[0].sizeBytes){throw 'Affected archive authentication failed'}
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try{foreach($entry in $zip.Entries){
        if(!$entry.FullName.StartsWith($name+'/',[StringComparison]::Ordinal)){throw 'Archive root differs from its canonical signed declaration'}
        $archiveEntries++
        if($entry.FullName.EndsWith('.mu',[StringComparison]::OrdinalIgnoreCase)){
            $url=$entry.FullName.Substring(0,$entry.FullName.Length-3)
            if(!$models.Add($url)){throw 'Duplicate model URL in affected archives'}
        }
    }}finally{$zip.Dispose()}
}
$text=[IO.File]::ReadAllText($ClientLogPath)
$matches=[regex]::Matches($text,"PartCompiler: Cannot clone model '([^']+)' as model does not exist")
if($matches.Count -ne 592){throw "Expected all 592 recorded model failures, found $($matches.Count)"}
$missing=[Collections.Generic.List[string]]::new()
$unique=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($match in $matches){$url=$match.Groups[1].Value;$null=$unique.Add($url);if(!$models.Contains($url)){$missing.Add($url)}}
if($missing.Count){$missing|Select-Object -First 15;throw 'Canonical archive paths do not resolve every recorded missing model'}
$attempt=Join-Path $PSScriptRoot ('BuildAttempts/ModelPaths-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $attempt|Out-Null
$result=[ordered]@{schemaVersion=1;version='kesas-rc27';passed=$true;capturedModelFailures=$matches.Count;uniqueFailedUrls=$unique.Count;allObservedModelUrlsResolveOrdinally=$true;affectedRoots=$names;affectedArchiveEntries=$archiveEntries;modelEntries=$models.Count;fullPackages=@($catalog.full.managedPackages).Count;potatoPackages=@($catalog.potato.managedPackages).Count;canonicalRoots=$roots.Count;allProfileArchives=$allPackages.Count;allProfilePayloadEntries=$allPayloadEntries;allProfileArchivePrefixCasingMatches=$true;allArchiveContentHashesRecomputed=$false;engineSha256=$engine.engineSha256;clientLogSha256=(Get-FileHash -LiteralPath $ClientLogPath -Algorithm SHA256).Hash.ToLowerInvariant();unityPrefabsInstantiated=$false}
[IO.File]::WriteAllText("$attempt/MODEL-PATH-RESULT.json",($result|ConvertTo-Json -Depth 8))
$result|ConvertTo-Json -Depth 8 -Compress
"MODEL-PATH-RESULT: $attempt/MODEL-PATH-RESULT.json"
