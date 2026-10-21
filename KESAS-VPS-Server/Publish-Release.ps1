[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReleaseZip,[string]$Notes='KESAS update',[switch]$AllowActivePlayers)
. "$PSScriptRoot/Update.Common.ps1"

function Add-ReleaseMedia([string]$ReleaseRoot,[string]$ConfigRoot,$Release,$Catalog) {
    # Only additive, content-addressed public archives. Operator configuration,
    # existing media, state and keys are never replacement targets here.
    $pending=[Collections.Generic.List[object]]::new()
    $config=Assert-UpdatePath $ConfigRoot
    if(-not [IO.Directory]::Exists($config)){throw 'Media destination must already exist.'}
    foreach($file in @($Release.files | Where-Object {$_.path -clike 'MediaPayload/*'})){
        if($file.path -cnotmatch '^MediaPayload/(kesas-[a-z0-9-]+-([a-f0-9]{64})\.zip)$' -or $Matches[2] -cne $file.sha256){throw 'Media filename must contain its full declared SHA256.'}
        $name=$Matches[1]
        $source=Assert-UpdatePath (Join-Path $ReleaseRoot $file.path)
        if(-not [IO.File]::Exists($source) -or ([IO.FileInfo]$source).Length -gt 512MB -or (Get-FileHash -LiteralPath $source).Hash -ine $file.sha256){throw 'Media source differs from the verified release.'}
        $references=@(foreach($profile in @($Catalog.full,$Catalog.potato)){foreach($package in @($profile.managedPackages)){if(([Uri]$package.archiveUrl).AbsolutePath -ceq ('/'+$name)){$package}}})
        if(!$references.Count -or @($references | Where-Object {$_.sha256 -cne $file.sha256 -or $_.sizeBytes -ne ([IO.FileInfo]$source).Length}).Count){throw 'Media must match every referring signed catalog declaration.'}
        Assert-GhpPublicZip $source
        $target=Assert-UpdatePath (Join-Path $config $name)
        if([IO.Directory]::Exists($target) -or ([IO.File]::Exists($target) -and (Get-FileHash -LiteralPath $target).Hash -ine $file.sha256)){throw 'Existing media is immutable; conflicting content will not be overwritten.'}
        $pending.Add([pscustomobject]@{source=$source;target=$target;sha256=$file.sha256})
    }
    # Validate the entire batch before its first append. New unreferenced media
    # may remain after a later publication failure; it never replaces live data.
    foreach($item in $pending){
        if([IO.File]::Exists($item.target)){continue}
        $temporary=Assert-UpdatePath ($item.target+'.incoming-'+[Guid]::NewGuid().ToString('N'))
        try{
            [IO.File]::Copy($item.source,$temporary,$false)
            if((Get-FileHash -LiteralPath $temporary).Hash -ine $item.sha256){throw 'Media changed during staging.'}
            try{[IO.File]::Move($temporary,$item.target)}catch{
                if(-not [IO.File]::Exists($item.target) -or (Get-FileHash -LiteralPath $item.target).Hash -ine $item.sha256){throw}
            }
        }finally{if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}}
    }
}

function Get-PublisherFingerprint($Root,$Config,[string]$ReleaseSha,[string]$ReleaseNotes) {
    [pscustomobject][ordered]@{
        releaseZipSha256=$ReleaseSha;notes=$ReleaseNotes;packOrigin=[string]$Config.packOrigin
        certificateSha256=([string]$Config.certificateSha256).ToLowerInvariant()
        publicKeySha256=(Get-FileHash -LiteralPath (Get-UpdateChildPath $Root 'secrets/update-signing-public.xml') -Algorithm SHA256).Hash.ToLowerInvariant()
        address=[string]$Config.address;gamePort=[int]$Config.gamePort
    }
}
function Get-PublisherPreparedNames([string]$Version) {
    @('Published/APFClientUpdate.json',"Published/APFClient-$Version.zip",'KESAS-Client-Bootstrap.zip',
        'KESAS-Client-Bootstrap.zip.sha256',"Aetherfront-Starter-$Version.zip",'install-catalog.json')
}
function Get-PublisherPreparedInventory([string]$Directory,[string]$Version) {
    foreach($relative in (Get-PublisherPreparedNames $Version)){
        $path=Get-UpdateChildPath $Directory $relative
        [pscustomobject]@{path=$relative;sizeBytes=([IO.FileInfo]$path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
}
function Assert-PublisherPrepared([string]$Directory,$Fingerprint,[string]$Version) {
    Assert-UpdateTree $Directory
    $record=Read-UpdateText (Get-UpdateChildPath $Directory 'prepared.json') | ConvertFrom-Json
    if($record.schema -ne 1 -or $record.version -cne $Version -or
        ($record.fingerprint|ConvertTo-Json -Depth 8 -Compress) -cne ($Fingerprint|ConvertTo-Json -Depth 8 -Compress)){
        throw 'Prepared release fingerprint mismatch. Preserve prepared files and review; no GitHub mutation requested.'
    }
    $names=@(Get-PublisherPreparedNames $Version)
    if(@($record.files).Count -ne $names.Count -or @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File).Count -ne $names.Count+1){throw 'Prepared release inventory mismatch; preserve files for review.'}
    foreach($actual in @(Get-PublisherPreparedInventory $Directory $Version)){
        $expected=@($record.files|Where-Object path -ceq $actual.path)
        if($expected.Count -ne 1 -or $expected[0].sha256 -cne $actual.sha256 -or $expected[0].sizeBytes -ne $actual.sizeBytes){throw 'Prepared release bytes changed; preserve files for review.'}
    }
    $bootstrapHash=(Get-FileHash -LiteralPath "$Directory/KESAS-Client-Bootstrap.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
    if((Get-FileHash -LiteralPath "$Directory/Aetherfront-Starter-$Version.zip" -Algorithm SHA256).Hash -ine $bootstrapHash -or
        (Read-UpdateText "$Directory/KESAS-Client-Bootstrap.zip.sha256").TrimEnd("`r","`n") -cne "$bootstrapHash  KESAS-Client-Bootstrap.zip"){
        throw 'Prepared bootstrap or checksum differs from the public starter.'
    }
}
function Assert-PublisherSigned($Root,$Config,[string]$Directory,[string]$Version) {
    $archives=@(Get-ChildItem -LiteralPath $Directory -Filter '*.zip' -File)
    if($archives.Count -ne 1 -or $archives[0].Name -cne "APFClient-$Version.zip"){throw 'Expected one signed update archive'}
    Invoke-UpdateTool $Root @('--verify','--manifest',"$Directory/APFClientUpdate.json",'--archive',$archives[0].FullName,'--public-key-xml',"$Root/secrets/update-signing-public.xml")
    $manifest=Read-UpdateText "$Directory/APFClientUpdate.json" | ConvertFrom-Json
    if($manifest.version -cne $Version -or $manifest.packageUrl -cne "$($Config.packOrigin)/updates/$Version/$($archives[0].Name)" -or @($manifest.files).Count -eq 0){throw 'Signed update feed has an unexpected version, package URL, or inventory'}
}
function Assert-PublisherHealth($Root,$Config) {
    # Require both listeners, each owned by its own deployment executable.
    $processes=@(Get-UpdateServiceProcesses $Root $Config)
    $gameIds=@($processes|Where-Object Path -ieq ([IO.Path]::GetFullPath("$Root/Server/Server.exe"))|ForEach-Object Id)
    $packIds=@($processes|Where-Object Path -ieq ([IO.Path]::GetFullPath("$Root/PackHost/ApfPackHost.exe"))|ForEach-Object Id)
    $udp=@(Get-NetUDPEndpoint -LocalPort $Config.gamePort -ErrorAction SilentlyContinue)
    $tcp=@(Get-NetTCPConnection -LocalPort $Config.packPort -State Listen -ErrorAction SilentlyContinue)
    if(!$gameIds.Count -or !$packIds.Count -or !$udp.Count -or !$tcp.Count){throw 'Native completion requires both owned services and listeners; no restart requested.'}
    foreach($endpoint in $udp){if($gameIds -notcontains $endpoint.OwningProcess){throw 'Native game listener ownership mismatch; no restart requested.'}}
    foreach($endpoint in $tcp){if($packIds -notcontains $endpoint.OwningProcess){throw 'Native pack listener ownership mismatch; no restart requested.'}}
    & "$Root/CertificateTool/ApfCertificateTool.exe" --probe-server "https://127.0.0.1:$($Config.packPort)/health" $Config.certificateSha256
    if($LASTEXITCODE -ne 0){throw 'Native completion pinned health check failed; no restart requested.'}
}
function Invoke-PublisherCompletion($Plan) {
    $result=@(Complete-GitHubPublication -Plan $Plan)
    if($result.Count -ne 1 -or $result[0].Published -isnot [bool] -or !$result[0].Published -or
        $result[0].Version -cne $Plan.Version -or $result[0].Repository -cne $Plan.Repository){throw 'GitHub completion was not confirmed; preserve the completion journal.'}
    $result[0]
}
function Read-PublisherPlayerStatus([string]$Path) {
    # The bridge briefly holds an exclusive write handle. Retry only Windows
    # sharing/locking conflicts; malformed, missing or oversized status fails
    # closed. Player freshness and both zero counts are checked by the caller.
    for($attempt=0;$attempt -lt 20;$attempt++) {
        $inputStream=$null
        try {
            $inputStream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            if($inputStream.Length -gt 4MB){throw 'Invalid authoritative player status; no services stopped.'}
            $reader=New-Object IO.StreamReader($inputStream)
            try{return ($reader.ReadToEnd()|ConvertFrom-Json)}finally{$reader.Dispose()}
        } catch {
            $cause=$_.Exception.GetBaseException()
            $nativeCode=$cause.HResult -band 65535
            if($cause -isnot [IO.IOException] -or $nativeCode -notin @(32,33) -or $attempt -eq 19){throw}
            Start-Sleep -Milliseconds 100
        } finally {if($null -ne $inputStream){$inputStream.Dispose()}}
    }
    throw 'Authoritative player status could not be read; no services stopped.'
}
function Assert-PublisherEmpty($Root,$Config) {
    # Preparation can outlast the bot's original empty-server check. Recheck
    # authoritative state immediately before the first service stop.
    $path=Get-UpdateChildPath $Root 'Server/APFDiscordBridge/status.json'
    $status=Read-PublisherPlayerStatus $path
    $updated=[DateTimeOffset]$status.updatedAt
    $age=([DateTimeOffset]::UtcNow-$updated.ToUniversalTime()).TotalSeconds
    $gameIds=@(Get-UpdateServiceProcesses $Root $Config|Where-Object Path -ieq ([IO.Path]::GetFullPath("$Root/Server/Server.exe"))|ForEach-Object Id)
    if($age -lt -2 -or $age -gt 10 -or $gameIds -notcontains $status.serverPid -or
        [string]$status.playerCount -cnotmatch '^0$' -or [string]$status.connectedCount -cnotmatch '^0$'){
        throw 'Player activity resumed or authoritative status is stale/invalid. Prepared release retained; no services stopped. Requeue when empty.'
    }
}

$root=Assert-UpdatePath $PSScriptRoot
$cfg=Get-UpdateDeployment $root
foreach($name in @('Server','PackHost','CertificateTool','Updates','Backups','.release-staging','.github-prepared','secrets','UpdateTool')){Assert-UpdateTree (Get-UpdateChildPath $root $name)}
$releaseLock=New-Object IO.FileStream((Get-UpdateChildPath $root '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$stage=Get-UpdateChildPath $root ('.release-staging/'+[Guid]::NewGuid().ToString('N'))
$journal=Get-UpdateChildPath $root '.update-transaction.json'
$backup=$null;$changed=$false;$stopped=$false;$started=$false;$published=$null;$journalWritten=$false
$activationLock=$null
$replacements=New-Object 'Collections.Generic.List[object]'
$states=New-Object 'Collections.Generic.List[object]'
$wasRunning=$false
$githubPlan=$null
$nativeReady=$false
try{
    $releasePath=Assert-UpdatePath (Resolve-Path -LiteralPath $ReleaseZip).Path
    $releaseSha=(Get-FileHash -LiteralPath $releasePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-UpdateKeyPair $root
    if([IO.File]::Exists($journal)){
        $pending=Read-UpdateText $journal | ConvertFrom-Json
        if(-not $pending.PSObject.Properties['phase'] -or $pending.phase -cne 'github-completion' -or $pending.schema -ne 1 -or
            $pending.releaseZipSha256 -cne $releaseSha -or $pending.version -cnotmatch '^kesas-rc[1-9][0-9]*$'){
            throw 'An unrelated or interrupted update needs review. Preserve .update-transaction.json; no service changes requested.'
        }
        $version=[string]$pending.version
        $fingerprint=Get-PublisherFingerprint $root $cfg $releaseSha $Notes
        $prepared=Get-UpdateChildPath $root ".github-prepared/$version"
        Assert-PublisherPrepared $prepared $fingerprint $version
        if(($pending.fingerprint|ConvertTo-Json -Depth 8 -Compress) -cne ($fingerprint|ConvertTo-Json -Depth 8 -Compress)){throw 'Completion journal fingerprint mismatch; review without rollback.'}
        if(-not [IO.File]::Exists("$root/GitHub.Publish.ps1")){throw 'GitHub publisher is not installed; preserve the completion journal.'}
        . "$root/GitHub.Publish.ps1"
        Assert-PublisherSigned $root $cfg "$prepared/Published" $version
        $catalog=Read-UpdateText "$prepared/install-catalog.json" | ConvertFrom-Json
        if(-not $catalog.gitHub.enabled){throw 'Completion journal has no enabled GitHub catalog.'}
        $fresh=New-GitHubPublicationPlan -Repository $catalog.gitHub.repository -MediaTag $catalog.gitHub.mediaTag -PublishedDirectory "$prepared/Published" -BootstrapZip "$prepared/Aetherfront-Starter-$version.zip" -CatalogPath "$prepared/install-catalog.json" -MediaDirectory "$root/Server/Config"
        if(($pending.githubPlan|ConvertTo-Json -Depth 16 -Compress) -cne ($fresh|ConvertTo-Json -Depth 16 -Compress)){throw 'Completion plan differs from the verified prepared release; review without rollback.'}
        $githubPlan=$fresh
        Assert-UpdateActiveFeed $root $cfg
        $livePack=Read-UpdateText "$root/Server/Config/APFPack.json" | ConvertFrom-Json
        $feedRecord=@($githubPlan.ReleaseAssets|Where-Object Kind -ceq 'feed')[0]
        if($livePack.requiredClientVersion -cne $version -or $livePack.packVersion -cne $version -or
            (Get-FileHash -LiteralPath "$root/Updates/APFClientUpdate.json" -Algorithm SHA256).Hash -ine $feedRecord.Sha256 -or
            (Get-FileHash -LiteralPath "$root/Updates/$version/APFClientUpdate.json" -Algorithm SHA256).Hash -ine $feedRecord.Sha256 -or
            (Get-FileHash -LiteralPath "$root/KESAS-Client-Bootstrap.zip" -Algorithm SHA256).Hash -ine (@($githubPlan.ReleaseAssets|Where-Object Kind -ceq 'bootstrap')[0].Sha256)){
            throw 'Active native release differs from the completion journal; review without rollback.'
        }
        Assert-PublisherHealth $root $cfg
        if((Get-FileHash -LiteralPath $releasePath -Algorithm SHA256).Hash -ine $releaseSha){throw 'Source release changed during completion verification; no GitHub mutation requested.'}
        $nativeReady=$true
        Invoke-PublisherCompletion $githubPlan
        [IO.File]::Delete($journal)
        Write-Host "Confirmed GitHub completion for $version. No services restarted."
        return
    }
    Assert-UpdateActiveFeed $root $cfg
    $wasRunning=@(Get-UpdateServiceProcesses $root $cfg).Count -gt 0
    Expand-UpdateArchive $releasePath "$stage/Release"
    $release=Read-UpdateText "$stage/Release/RELEASE.json" | ConvertFrom-Json
    [long]$releaseNumber=0
    if($release.version -cnotmatch '^kesas-rc[1-9][0-9]*$' -or ![long]::TryParse($release.version.Substring(8),[ref]$releaseNumber)){throw 'Release version must be kesas-rc followed by a positive integer within Int64 range'}
    $version=$release.version
    $published=Get-UpdateChildPath $root "Updates/$version"
    if(Test-Path -LiteralPath $published){throw 'This release version is immutable. Use a new version for changed files'}
    $currentFeed=Get-UpdateChildPath $root 'Updates/APFClientUpdate.json'
    if([IO.File]::Exists($currentFeed)){
        $current=Read-UpdateText $currentFeed | ConvertFrom-Json
        [long]$currentNumber=0
        if($current.version -cnotmatch '^kesas-rc[1-9][0-9]*$' -or ![long]::TryParse($current.version.Substring(8),[ref]$currentNumber)){throw 'Invalid active feed version; investigate without replacing the feed'}
        if($releaseNumber -le $currentNumber){throw 'Publish a newer release version'}
    }
    $declared=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $release.files){
        $relative=Get-UpdateRelativePath $file.path
        if(!$declared.Add($relative)){throw 'Duplicate release manifest entry'}
        if(@('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json') -cnotcontains $relative -and $relative -cnotmatch '^(ServerPayload|PackHostPayload|CertificateToolPayload)/' -and $relative -cnotmatch '^MediaPayload/kesas-[a-z0-9-]+-[a-f0-9]{64}\.zip$'){throw "Unsupported release file: $relative"}
        if($relative -match '^(ServerPayload|PackHostPayload|CertificateToolPayload)/(Config|Universe|Aetherfront|Logs|secrets)(/|$)'){throw 'Release contains protected server state'}
        if($file.sha256 -notmatch '^[a-fA-F0-9]{64}$'){throw "Invalid release SHA256: $relative"}
        $path=Get-UpdateChildPath "$stage/Release" $relative
        if(![IO.File]::Exists($path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256){throw "Release hash mismatch: $relative"}
    }
    foreach($required in @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')){if(!$declared.Contains($required)){throw "Missing required release file: $required"}}
    if(@(Get-ChildItem -LiteralPath "$stage/Release" -Recurse -Force -File).Count -ne $declared.Count+1){throw 'Release contains undeclared files'}
    Expand-UpdateArchive "$stage/Release/ClientTemplate.zip" "$stage/Client"
    $clientCatalog=Read-UpdateText "$stage/Client/GameData/LunaMultiplayer/Launcher/install-catalog.json" | ConvertFrom-Json
    $githubSettings=$clientCatalog.PSObject.Properties['gitHub']
    $githubEnabled=$githubSettings -and $githubSettings.Value.enabled
    $fingerprint=$null;$prepared=$null;$reuse=$false
    $bootstrapSource="$stage/KESAS-Client-Bootstrap.zip"
    if($githubEnabled){
        if(-not [IO.File]::Exists("$root/GitHub.Publish.ps1")){throw 'GitHub publisher is not installed; no services have been stopped.'}
        . "$root/GitHub.Publish.ps1"
        $fingerprint=Get-PublisherFingerprint $root $cfg $releaseSha $Notes
        $prepared=Get-UpdateChildPath $root ".github-prepared/$version"
        if(Test-Path -LiteralPath $prepared){
            Assert-PublisherPrepared $prepared $fingerprint $version
            Copy-UpdateTree "$prepared/Published" "$stage/Published"
            $bootstrapSource="$prepared/KESAS-Client-Bootstrap.zip"
            $reuse=$true
        }
    }
    if(@($release.files | Where-Object {$_.path -clike 'MediaPayload/*'}).Count){
        if(-not $githubEnabled){. "$root/GitHub.Publish.ps1"}
        Add-ReleaseMedia "$stage/Release" "$root/Server/Config" $release $clientCatalog
    }
    if(-not $reuse){
        New-UpdateDirectory "$stage/Published"
        Invoke-UpdateTool $root @('--root-overlay','--source-root',"$stage/Client",'--output',"$stage/Published",'--version',$version,'--package-base-url',"$($cfg.packOrigin)/updates/$version/",'--private-key-xml',"$root/secrets/update-signing-private.xml",'--ksp-version','1.12.5','--release-notes',$Notes)
        & "$root/Export-Client.ps1" -Template "$stage/Release/ClientTemplate.zip" -Output $bootstrapSource -PublicationLockHeld
    }
    Assert-PublisherSigned $root $cfg "$stage/Published" $version
    $oldPack=Read-UpdateText "$root/Server/Config/APFPack.json" | ConvertFrom-Json
    $next=Read-UpdateText "$stage/Release/APFPack-template.json" | ConvertFrom-Json
    if($next.requiredClientVersion -cne $version){throw 'Pack template client version differs from the release version'}
    $next.serverId=$oldPack.serverId;$next.packId=$oldPack.packId
    Write-UpdateAtomic "$stage/pack.json" ($next|ConvertTo-Json -Depth 40)
    if($githubEnabled){
        # Upload immutable media and a draft of these exact signed bytes before
        # any service is stopped. Never publish a candidate rebuilt elsewhere.
        if(-not $reuse){
            New-UpdateDirectory $prepared
            Copy-UpdateTree "$stage/Published" "$prepared/Published"
            Copy-UpdateAtomic $bootstrapSource "$prepared/KESAS-Client-Bootstrap.zip"
            Copy-UpdateAtomic "$bootstrapSource.sha256" "$prepared/KESAS-Client-Bootstrap.zip.sha256"
            Copy-UpdateAtomic $bootstrapSource "$prepared/Aetherfront-Starter-$version.zip"
            Copy-UpdateAtomic "$stage/Client/GameData/LunaMultiplayer/Launcher/install-catalog.json" "$prepared/install-catalog.json"
            Write-UpdateAtomic "$prepared/prepared.json" (@{schema=1;version=$version;fingerprint=$fingerprint;files=@(Get-PublisherPreparedInventory $prepared $version)}|ConvertTo-Json -Depth 16)
            $bootstrapSource="$prepared/KESAS-Client-Bootstrap.zip"
        }
        Assert-PublisherPrepared $prepared $fingerprint $version
        $githubPlan=New-GitHubPublicationPlan -Repository $githubSettings.Value.repository -MediaTag $githubSettings.Value.mediaTag -PublishedDirectory "$prepared/Published" -BootstrapZip "$prepared/Aetherfront-Starter-$version.zip" -CatalogPath "$prepared/install-catalog.json" -MediaDirectory "$root/Server/Config"
        if((Get-FileHash -LiteralPath $releasePath -Algorithm SHA256).Hash -ine $releaseSha){throw 'Source release changed during preparation; no GitHub mutation requested.'}
        Prepare-GitHubPublication -Plan $githubPlan
    }
    $targets=@(
        @{source="$stage/Release/ClientTemplate.zip";relative='ClientTemplate.zip'},
        @{source="$stage/Release/AetherfrontTechnology.json";relative='Server/Config/AetherfrontTechnology.json'},
        @{source="$stage/pack.json";relative='Server/Config/APFPack.json'},
        @{source=$bootstrapSource;relative='KESAS-Client-Bootstrap.zip'},
        @{source="$bootstrapSource.sha256";relative='KESAS-Client-Bootstrap.zip.sha256'},
        @{source="$stage/Published/APFClientUpdate.json";relative='Updates/APFClientUpdate.json'}
    )
    $payloadFamilies=@{'ServerPayload/'='Server/';'PackHostPayload/'='PackHost/';'CertificateToolPayload/'='CertificateTool/'}
    foreach($file in $release.files){
        foreach($prefix in $payloadFamilies.Keys){
            if($file.path.StartsWith($prefix,[StringComparison]::Ordinal)){
                $targets+=@{source=(Get-UpdateChildPath "$stage/Release" $file.path);relative=($payloadFamilies[$prefix]+$file.path.Substring($prefix.Length))}
                break
            }
        }
    }
    foreach($item in $targets){
        $destination=Get-UpdateChildPath $root $item.relative
        if([IO.Directory]::Exists($destination)){throw "Expected a file destination: $($item.relative)"}
    }
    if($wasRunning){
        if(-not $AllowActivePlayers){Assert-PublisherEmpty $root $cfg}
        Wait-UpdateServerStopped $root $cfg;$stopped=$true
    }
    $activationLock=New-Object IO.FileStream((Get-UpdateChildPath $root '.server.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $backup=Get-UpdateChildPath $root ("Backups/updates/$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')-$version-"+[Guid]::NewGuid().ToString('N'))
    New-UpdateDirectory $backup
    foreach($name in @('Aetherfront','Universe')){
        $statePath=Get-UpdateChildPath $root "Server/$name"
        $exists=[IO.Directory]::Exists($statePath)
        if($exists){Copy-UpdateTree $statePath "$backup/$name"}
        $states.Add(@{name=$name;target=$statePath;existed=$exists})
    }
    foreach($item in $targets){
        $destination=Get-UpdateChildPath $root $item.relative
        $old=Get-UpdateChildPath $backup ('files/'+$item.relative)
        $existed=[IO.File]::Exists($destination)
        if($existed){Copy-UpdateAtomic $destination $old}
        $replacements.Add(@{target=$destination;backup=$old;existed=$existed})
    }
    $transaction=@{schema=1;phase='native-activation';releaseZipSha256=$releaseSha;fingerprint=$fingerprint;githubPlan=$githubPlan;backup=$backup;version=$version;wasRunning=$wasRunning;files=@($replacements.ToArray());states=@($states.ToArray())}
    Write-UpdateAtomic $journal ($transaction|ConvertTo-Json -Depth 16)
    $journalWritten=$true
    New-UpdateDirectory "$root/Updates"
    [IO.Directory]::Move("$stage/Published",$published)
    $changed=$true
    foreach($item in $targets | Where-Object {$_.relative -ne 'Updates/APFClientUpdate.json'}){Copy-UpdateAtomic $item.source (Get-UpdateChildPath $root $item.relative)}
    if($wasRunning){$activationLock.Dispose();$activationLock=$null;$started=$true;Start-UpdatedServer $root $cfg}
    # The stable feed becomes visible only after every file and service is ready.
    Copy-UpdateAtomic "$published/APFClientUpdate.json" $currentFeed
    if($githubPlan){
        # Native readiness is the no-rollback boundary, including journal-write
        # failure. No remote completion is attempted without a durable marker.
        $nativeReady=$true
        $transaction.phase='github-completion'
        Write-UpdateAtomic $journal ($transaction|ConvertTo-Json -Depth 16)
        Invoke-PublisherCompletion $githubPlan
    }
    [IO.File]::Delete($journal);$journalWritten=$false
    Write-Host "Published $version. Clients receive the signed update from $($cfg.packOrigin)/updates/APFClientUpdate.json"
    Write-Host "Recovery backup: $backup"
}catch{
    $failure=$_
    if($githubPlan -and $nativeReady){
        Write-Warning 'Native release remains active. Preserve prepared files and .update-transaction.json. Retry the same release and notes for github-completion phase; otherwise review the interrupted journal. No rollback requested.'
        throw $failure
    }
    $recoveryErrors=New-Object 'Collections.Generic.List[string]'
    $safeToRestore=$true
    if($changed){
        if($started){try{Wait-UpdateServerStopped $root $cfg}catch{$recoveryErrors.Add($_.Exception.Message);$safeToRestore=$false}}
        if($safeToRestore -and !$activationLock){
            try{$activationLock=New-Object IO.FileStream((Get-UpdateChildPath $root '.server.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
            catch{$recoveryErrors.Add($_.Exception.Message);$safeToRestore=$false}
        }
        if($safeToRestore){
            foreach($item in $replacements){
                try{
                    Assert-UpdatePath $item.target | Out-Null
                    if($item.existed){Copy-UpdateAtomic $item.backup $item.target}elseif([IO.File]::Exists($item.target)){[IO.File]::Delete($item.target)}
                }catch{$recoveryErrors.Add($_.Exception.Message)}
            }
            foreach($state in $states){
                try{
                    Assert-UpdateTree $state.target
                    if([IO.Directory]::Exists($state.target)){[IO.Directory]::Move($state.target,"$backup/failed-$($state.name)")}
                    if($state.existed){Copy-UpdateTree "$backup/$($state.name)" $state.target}
                }catch{$recoveryErrors.Add($_.Exception.Message)}
            }
            # Failed activation never becomes a permanent published version.
            try{Assert-UpdateTree $published;if([IO.Directory]::Exists($published)){[IO.Directory]::Move($published,"$backup/failed-published")}}catch{$recoveryErrors.Add($_.Exception.Message)}
        }
    }
    if($activationLock){$activationLock.Dispose();$activationLock=$null}
    if($wasRunning -and $stopped -and $safeToRestore -and !$recoveryErrors.Count){try{Start-UpdatedServer $root $cfg}catch{$recoveryErrors.Add($_.Exception.Message)}}
    if($journalWritten -and !$recoveryErrors.Count){[IO.File]::Delete($journal);$journalWritten=$false}
    foreach($errorText in $recoveryErrors){Write-Warning "Recovery needs attention: $errorText. Preserve $backup and .update-transaction.json"}
    throw $failure
}finally{
    if($activationLock){$activationLock.Dispose()}
    $releaseLock.Dispose()
    $resolved=Get-UpdateChildPath $root ('.release-staging/'+[IO.Path]::GetFileName($stage))
    Assert-UpdateTree $resolved
    if([IO.Directory]::Exists($resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
