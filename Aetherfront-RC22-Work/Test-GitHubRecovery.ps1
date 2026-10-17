[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$publisher=Join-Path $PSScriptRoot 'NativePublisher/Publish-Release.ps1'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('APF-GitHubRecovery-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($testRoot)
$script:Cases=0;$script:Passed=0
$script:Failures=[Collections.Generic.List[string]]::new()
$script:ProbeCommands=[Collections.Generic.List[string]]::new()
function Assert-Test([bool]$Condition,[string]$Message){if(!$Condition){throw "Assertion failed: $Message"}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern){
    $message=$null
    try{$null=& $Action}catch{$message=$_.Exception.Message}
    Assert-Test ($null -ne $message) 'Expected rejection'
    Assert-Test ($message -match $Pattern) "Unexpected rejection: $message"
}
function Test-Case([string]$Name,[scriptblock]$Action){
    try{& $Action;$script:Passed++;Write-Output "PASS $Name"}
    catch{$script:Failures.Add("$Name : $($_.Exception.Message) at $($_.ScriptStackTrace)");Write-Output "FAIL $Name : $($_.Exception.Message)"}
}
function Write-TestText([string]$Path,[string]$Value){
    $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path,$Value,[Text.UTF8Encoding]::new($false))
}
function Write-TestJson([string]$Path,$Value){Write-TestText $Path ($Value|ConvertTo-Json -Depth 30)}
function Write-TestZip([string]$Path,$Entries){
    $zip=[IO.Compression.ZipFile]::Open($Path,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($name in $Entries.Keys){
        $entry=$zip.CreateEntry($name);$entry.LastWriteTime=[DateTimeOffset]'2020-01-01T00:00:00Z'
        $stream=$entry.Open()
        try{$bytes=$Entries[$name];if($bytes -isnot [byte[]]){$bytes=[Text.Encoding]::UTF8.GetBytes([string]$bytes)};$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    }}finally{$zip.Dispose()}
}

# Every publisher dependency is supplied inside an isolated temporary deployment.
# There is no production library, gh process, service control, socket, or probe.
$common=@'
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert-UpdatePath([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if($full -ine $global:PublisherRecoveryFake.Root -and !$full.StartsWith($global:PublisherRecoveryFake.Root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture path escaped deployment'}
    $full
}
function Assert-UpdateTree([string]$Path){$null=Assert-UpdatePath $Path}
function Get-UpdateRelativePath([string]$Path){if($Path -match '(^|[\\/])\.\.([\\/]|$)') {throw 'Unsafe fixture relative path'};$Path}
function Get-UpdateChildPath([string]$Root,[string]$Relative){$null=Get-UpdateRelativePath $Relative;Assert-UpdatePath (Join-Path $Root $Relative)}
function New-UpdateDirectory([string]$Path){$null=[IO.Directory]::CreateDirectory((Assert-UpdatePath $Path))}
function Read-UpdateText([string]$Path,[long]$MaximumBytes=16777216){[IO.File]::ReadAllText((Assert-UpdatePath $Path))}
function Write-UpdateAtomic([string]$Path,[string]$Text){
    $full=Assert-UpdatePath $Path
    if($global:PublisherRecoveryFake.FailMarkerWrite -and $full.EndsWith('.update-transaction.json') -and ($Text|ConvertFrom-Json).phase -ceq 'github-completion'){throw 'fixture completion marker write failed'}
    New-UpdateDirectory ([IO.Path]::GetDirectoryName($full))
    [IO.File]::WriteAllText($full,$Text,[Text.UTF8Encoding]::new($false))
}
function Copy-UpdateAtomic([string]$Source,[string]$Destination){
    $from=Assert-UpdatePath $Source;$to=Assert-UpdatePath $Destination
    if($global:PublisherRecoveryFake.FailActivation -and $to -ieq (Join-Path $global:PublisherRecoveryFake.Root 'Server/Config/AetherfrontTechnology.json')){
        $global:PublisherRecoveryFake.FailActivation=$false;throw 'fixture native activation failed'
    }
    New-UpdateDirectory ([IO.Path]::GetDirectoryName($to));[IO.File]::Copy($from,$to,$true)
}
function Copy-UpdateTree([string]$Source,[string]$Destination){
    $from=Assert-UpdatePath $Source;$to=Assert-UpdatePath $Destination;New-UpdateDirectory $to
    foreach($file in [IO.Directory]::EnumerateFiles($from,'*',[IO.SearchOption]::AllDirectories)){
        $relative=$file.Substring($from.TrimEnd('\','/').Length+1);Copy-UpdateAtomic $file (Join-Path $to $relative)
    }
}
function Expand-UpdateArchive([string]$Archive,[string]$Destination){
    New-UpdateDirectory ([IO.Path]::GetDirectoryName((Assert-UpdatePath $Destination)))
    [IO.Compression.ZipFile]::ExtractToDirectory((Assert-UpdatePath $Archive),(Assert-UpdatePath $Destination))
}
function Get-UpdateDeployment([string]$Root){Read-UpdateText "$Root/deployment.json"|ConvertFrom-Json}
function Assert-UpdateKeyPair([string]$Root){$global:PublisherRecoveryFake.KeyChecks++}
function Get-FakeOption([string[]]$Arguments,[string]$Name){$i=[Array]::IndexOf($Arguments,$Name);if($i -lt 0){throw "Missing fixture argument $Name"};$Arguments[$i+1]}
function Invoke-UpdateTool([string]$Root,[string[]]$Arguments){
    $fake=$global:PublisherRecoveryFake
    if($Arguments -contains '--verify'){
        $fake.Verifications++
        if($fake.RejectSignature){throw 'fixture signature rejected'}
        $feed=Read-UpdateText (Get-FakeOption $Arguments '--manifest')|ConvertFrom-Json
        $archive=Get-FakeOption $Arguments '--archive'
        if($feed.signature -cne 'fixture-signed' -or $feed.sha256 -ine (Get-FileHash -LiteralPath $archive).Hash -or $feed.sizeBytes -ne ([IO.FileInfo]$archive).Length){throw 'fixture signed archive verification failed'}
        return
    }
    $fake.Builds++
    $source=Get-FakeOption $Arguments '--source-root';$output=Get-FakeOption $Arguments '--output';$version=Get-FakeOption $Arguments '--version'
    New-UpdateDirectory $output
    $archive="$output/APFClient-$version.zip"
    $zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
    $records=@(try{foreach($file in @(Get-ChildItem -LiteralPath $source -Recurse -File|Sort-Object FullName)){
        $name=$file.FullName.Substring($source.TrimEnd('\','/').Length+1).Replace('\','/')
        $entry=$zip.CreateEntry($name)
        $entry.LastWriteTime=([DateTimeOffset]'2020-01-01T00:00:00Z').AddSeconds(2*$fake.Builds)
        $inputStream=[IO.File]::OpenRead($file.FullName);$outputStream=$entry.Open()
        try{$inputStream.CopyTo($outputStream)}finally{$outputStream.Dispose();$inputStream.Dispose()}
        [pscustomobject]@{path=$name;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant();sizeBytes=$file.Length}
    }}finally{$zip.Dispose()})
    $feed=[ordered]@{version=$version;packageUrl=(Get-FakeOption $Arguments '--package-base-url')+"APFClient-$version.zip";sha256=(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant();sizeBytes=([IO.FileInfo]$archive).Length;signature='fixture-signed';files=$records;releaseNotes=(Get-FakeOption $Arguments '--release-notes')}
    Write-UpdateAtomic "$output/APFClientUpdate.json" ($feed|ConvertTo-Json -Depth 20)
}
function Assert-UpdateActiveFeed([string]$Root,$Config){
    $feed=Read-UpdateText "$Root/Updates/APFClientUpdate.json"|ConvertFrom-Json
    if($feed.packageUrl -cne "$($Config.packOrigin)/updates/$($feed.version)/APFClient-$($feed.version).zip"){throw 'fixture active origin mismatch'}
    Invoke-UpdateTool $Root @('--verify','--manifest',"$Root/Updates/APFClientUpdate.json",'--archive',"$Root/Updates/$($feed.version)/APFClient-$($feed.version).zip")
}
function Get-UpdateServiceProcesses([string]$Root,$Config){
    if($global:PublisherRecoveryFake.Running){
        [pscustomobject]@{Id=1001;Path=[IO.Path]::GetFullPath("$Root/Server/Server.exe")}
        [pscustomobject]@{Id=1002;Path=[IO.Path]::GetFullPath("$Root/PackHost/ApfPackHost.exe")}
    }
}
function Wait-UpdateServerStopped([string]$Root,$Config){$global:PublisherRecoveryFake.Stops++;$global:PublisherRecoveryFake.Running=$false}
function Start-UpdatedServer([string]$Root,$Config){$global:PublisherRecoveryFake.Starts++;$global:PublisherRecoveryFake.Running=$true}
function Get-NetUDPEndpoint {
    [CmdletBinding()]param([int]$LocalPort)
    if($global:PublisherRecoveryFake.Running -and !$global:PublisherRecoveryFake.MissingListener){[pscustomobject]@{OwningProcess=1001}}
}
function Get-NetTCPConnection {
    [CmdletBinding()]param([int]$LocalPort,[string]$State)
    if($global:PublisherRecoveryFake.Running -and !$global:PublisherRecoveryFake.MissingListener){[pscustomobject]@{OwningProcess=$(if($global:PublisherRecoveryFake.ForeignListener){9999}else{1002})}}
}
'@
$export=@'
param([string]$Template,[string]$Output,[switch]$PublicationLockHeld)
$global:PublisherRecoveryFake.Exports++
Copy-UpdateAtomic $Template $Output
$zip=[IO.Compression.ZipFile]::Open($Output,[IO.Compression.ZipArchiveMode]::Update)
try{
    $entry=$zip.CreateEntry('fixture-public-server.txt')
    $entry.LastWriteTime=([DateTimeOffset]'2020-01-01T00:00:00Z').AddSeconds(2*$global:PublisherRecoveryFake.Exports)
    $writer=[IO.StreamWriter]::new($entry.Open());try{$writer.Write('public fixture server')}finally{$writer.Dispose()}
}finally{$zip.Dispose()}
Write-UpdateAtomic "$Output.sha256" ((Get-FileHash -LiteralPath $Output).Hash.ToLowerInvariant()+"  KESAS-Client-Bootstrap.zip`n")
'@
$library=@'
function New-GitHubPublicationPlan {
    param([string]$Repository,[string]$MediaTag,[string]$PublishedDirectory,[string]$BootstrapZip,[string]$CatalogPath,[string]$MediaDirectory)
    $global:PublisherRecoveryFake.Plans++
    $feed=Read-UpdateText "$PublishedDirectory/APFClientUpdate.json"|ConvertFrom-Json
    $catalog=Read-UpdateText $CatalogPath|ConvertFrom-Json
    if($catalog.version -cne $feed.version){throw 'fixture catalog mismatch'}
    $zip=[IO.Compression.ZipFile]::OpenRead($BootstrapZip)
    try{foreach($file in $feed.files){
        $entry=$zip.GetEntry($file.path)
        if(!$entry -or $entry.Length -ne $file.sizeBytes){throw 'fixture bootstrap inventory mismatch'}
        $sha=[Security.Cryptography.SHA256]::Create();$stream=$entry.Open()
        try{$digest=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$sha.Dispose()}
        if($digest -cne $file.sha256){throw 'fixture bootstrap inventory mismatch'}
    }}finally{$zip.Dispose()}
    $assets=@(foreach($item in @(@{name='APFClientUpdate.json';kind='feed';path="$PublishedDirectory/APFClientUpdate.json"},@{name="APFClient-$($feed.version).zip";kind='client';path="$PublishedDirectory/APFClient-$($feed.version).zip"},@{name=[IO.Path]::GetFileName($BootstrapZip);kind='bootstrap';path=$BootstrapZip})){
        [pscustomobject]@{Path=[IO.Path]::GetFullPath($item.path);Name=$item.name;Size=([IO.FileInfo]$item.path).Length;Sha256=(Get-FileHash -LiteralPath $item.path).Hash.ToLowerInvariant();Kind=$item.kind;ReleaseTag=$feed.version;Url="https://github.com/$Repository/releases/download/$($feed.version)/$($item.name)"}
    })
    [pscustomobject]@{Repository=$Repository;MediaTag=$MediaTag;Version=$feed.version;PublishedDirectory=[IO.Path]::GetFullPath($PublishedDirectory);BootstrapZip=[IO.Path]::GetFullPath($BootstrapZip);CatalogPath=[IO.Path]::GetFullPath($CatalogPath);MediaDirectory=[IO.Path]::GetFullPath($MediaDirectory);Assets=$assets;MediaAssets=@();ReleaseAssets=$assets}
}
function Prepare-GitHubPublication {
    param($Plan)
    $fake=$global:PublisherRecoveryFake;$fake.Prepares++
    foreach($asset in $Plan.ReleaseAssets){
        if($fake.Remote.ContainsKey($asset.Name) -and $fake.Remote[$asset.Name] -cne $asset.Sha256){throw 'fixture immutable asset mismatch'}
        $fake.Remote[$asset.Name]=$asset.Sha256
    }
    if($fake.FailPrepareCount -gt 0){$fake.FailPrepareCount--;throw 'fixture prepare unknown outcome'}
}
function Complete-GitHubPublication {
    param($Plan)
    $fake=$global:PublisherRecoveryFake;$fake.Completions++
    $journal=Read-UpdateText "$($fake.Root)/.update-transaction.json"|ConvertFrom-Json
    if($journal.phase -cne 'github-completion' -or $journal.releaseZipSha256 -cne (Get-FileHash -LiteralPath "$($fake.Root)/release.zip").Hash.ToLowerInvariant()){throw 'fixture completion marker missing'}
    $fake.MarkerSeen=$true;$fake.RemotePublished=$true
    if($fake.FailCompleteCount -gt 0){$fake.FailCompleteCount--;throw 'fixture publication readback unavailable'}
    [pscustomobject]@{Repository=$Plan.Repository;Version=$Plan.Version;Published=$true}
}
'@
function New-Fixture {
    $script:Cases++
    $root=Join-Path $testRoot "case-$script:Cases"
    $null=[IO.Directory]::CreateDirectory($root)
    $global:PublisherRecoveryFake=@{Root=$root;Builds=0;Exports=0;Prepares=0;Plans=0;Completions=0;Stops=0;Starts=0;HealthChecks=0;KeyChecks=0;Verifications=0;Running=$true;MissingListener=$false;ForeignListener=$false;HealthFails=$false;RejectSignature=$false;FailPrepareCount=0;FailCompleteCount=0;FailActivation=$false;FailMarkerWrite=$false;MarkerSeen=$false;RemotePublished=$false;Remote=@{}}
    foreach($dir in @('Server/Config','Server/Universe','Server/Aetherfront','PackHost','CertificateTool','secrets','Updates/kesas-rc21','Backups','UpdateTool')){$null=[IO.Directory]::CreateDirectory((Join-Path $root $dir))}
    [IO.File]::Copy($publisher,"$root/Publish-Release.ps1")
    Write-TestText "$root/Update.Common.ps1" $common
    Write-TestText "$root/Export-Client.ps1" $export
    Write-TestText "$root/GitHub.Publish.ps1" $library
    Write-TestText "$root/secrets/update-signing-public.xml" 'fixture-public-key'
    Write-TestJson "$root/deployment.json" ([ordered]@{address='127.0.0.1';gamePort=18865;packPort=18866;packOrigin='https://127.0.0.1:18866';certificateSha256=('a'*64)})
    Write-TestJson "$root/Server/Config/APFPack.json" @{packVersion='kesas-rc21';requiredClientVersion='kesas-rc21';serverId='fixture-server';packId='fixture-pack'}
    Write-TestText "$root/Server/Config/AetherfrontTechnology.json" 'old technology'
    Write-TestText "$root/Server/Universe/persistent.sfs" 'preserve universe'
    Write-TestText "$root/Server/Aetherfront/state.json" 'preserve server state'
    Write-TestJson "$root/Server/APFDiscordBridge/status.json" @{serverPid=1001;updatedAt=[DateTimeOffset]::UtcNow.ToString('o');playerCount=0;connectedCount=0}
    Write-TestText "$root/KESAS-Client-Bootstrap.zip" 'old bootstrap'
    Write-TestText "$root/KESAS-Client-Bootstrap.zip.sha256" 'old checksum'
    Write-TestText "$root/ClientTemplate.zip" 'old template'
    $old="$root/Updates/kesas-rc21/APFClient-kesas-rc21.zip"
    Write-TestZip $old @{'old.txt'='old client'}
    Write-TestJson "$root/Updates/APFClientUpdate.json" @{version='kesas-rc21';packageUrl='https://127.0.0.1:18866/updates/kesas-rc21/APFClient-kesas-rc21.zip';signature='fixture-signed';sha256=(Get-FileHash -LiteralPath $old).Hash.ToLowerInvariant();sizeBytes=([IO.FileInfo]$old).Length}
    $catalog=[ordered]@{version='kesas-rc22';gitHub=[ordered]@{enabled=$true;repository='fixture/public';mediaTag='pack-media'};full=@{managedPackages=@()};potato=@{managedPackages=@()}}
    $template="$root/fixture-template.zip"
    Write-TestZip $template ([ordered]@{'GameData/LunaMultiplayer/Launcher/install-catalog.json'=($catalog|ConvertTo-Json -Depth 20);'GameData/LunaMultiplayer/Data/APFClient.json'='{"clientVersion":"kesas-rc22"}'})
    $payload=[ordered]@{'ClientTemplate.zip'=[IO.File]::ReadAllBytes($template);'APFPack-template.json'=(@{packVersion='kesas-rc22';requiredClientVersion='kesas-rc22';serverId='replace';packId='replace'}|ConvertTo-Json);'AetherfrontTechnology.json'='new technology'}
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$files=@(foreach($name in $payload.Keys){$bytes=$payload[$name];if($bytes -isnot [byte[]]){$bytes=[Text.Encoding]::UTF8.GetBytes([string]$bytes)};@{path=$name;sha256=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()}})}finally{$sha.Dispose()}
    $payload['RELEASE.json']=(@{version='kesas-rc22';files=$files}|ConvertTo-Json -Depth 10)
    Write-TestZip "$root/release.zip" $payload
    $probeCommand="$root/CertificateTool/ApfCertificateTool.exe"
    Set-Item -LiteralPath ('Function:\global:'+$probeCommand) -Value {
        param([Parameter(ValueFromRemainingArguments=$true)]$ProbeArguments)
        $global:PublisherRecoveryFake.HealthChecks++
        if($ProbeArguments.Count -ne 3 -or $ProbeArguments[0] -cne '--probe-server' -or $ProbeArguments[1] -cne 'https://127.0.0.1:18866/health' -or $ProbeArguments[2] -cne ('a'*64)){throw 'Unexpected fixture health invocation'}
        $global:LASTEXITCODE=$(if($global:PublisherRecoveryFake.HealthFails){7}else{0})
    }
    $script:ProbeCommands.Add($probeCommand)
    [pscustomobject]@{Root=$root;Publisher="$root/Publish-Release.ps1";Release="$root/release.zip";Prepared="$root/.github-prepared/kesas-rc22";Journal="$root/.update-transaction.json";Template=$template;Fake=$global:PublisherRecoveryFake}
}
function Invoke-Fixture($Fixture,[string]$ReleaseNotes='fixture notes'){
    $null=& $Fixture.Publisher -ReleaseZip $Fixture.Release -Notes $ReleaseNotes
}
function Get-LiveVersion($Fixture){([IO.File]::ReadAllText("$($Fixture.Root)/Updates/APFClientUpdate.json")|ConvertFrom-Json).version}
function New-PendingFixture {
    $case=New-Fixture;$case.Fake.FailCompleteCount=1
    Assert-Throws {Invoke-Fixture $case} 'publication readback unavailable'
    $case
}
try{
    foreach($guard in @('playerCount','connectedCount','stale','foreignPid')){
        Test-Case "pre-stop gate rejects $guard after preparation without stopping" {
            $case=New-Fixture
            $status=@{serverPid=1001;updatedAt=[DateTimeOffset]::UtcNow.ToString('o');playerCount=0;connectedCount=0}
            switch($guard){
                'stale'{$status.updatedAt=[DateTimeOffset]::UtcNow.AddMinutes(-2).ToString('o')}
                'foreignPid'{$status.serverPid=9999}
                default{$status[$guard]=1}
            }
            Write-TestJson "$($case.Root)/Server/APFDiscordBridge/status.json" $status
            Assert-Throws {Invoke-Fixture $case} 'Player activity resumed or authoritative status is stale/invalid'
            Assert-Test ($case.Fake.Prepares -eq 1 -and $case.Fake.Stops -eq 0 -and $case.Fake.Completions -eq 0) 'Late gate preserved all running services'
            Assert-Test ((Get-LiveVersion $case) -ceq 'kesas-rc21' -and (Test-Path -LiteralPath "$($case.Prepared)/prepared.json")) 'Late gate retained prepared bytes and active old feed'
        }
    }
    Test-Case 'prepared bytes survive failed preparation and a timestamp-different second export' {
        $case=New-Fixture;$case.Fake.FailPrepareCount=1
        Assert-Throws {Invoke-Fixture $case} 'prepare unknown outcome'
        Assert-Test ($case.Fake.Stops -eq 0 -and (Get-LiveVersion $case) -ceq 'kesas-rc21') 'Preparation failure leaves native deployment alone'
        $first=(Get-FileHash -LiteralPath "$($case.Prepared)/KESAS-Client-Bootstrap.zip").Hash
        # Export again deliberately; only timestamps differ. The retry must not
        # select this regenerated archive or call the exporter a third time.
        & {param($fixture)
            . "$($fixture.Root)/Update.Common.ps1"
            & "$($fixture.Root)/Export-Client.ps1" -Template $fixture.Template -Output "$($fixture.Root)/comparison.zip" -PublicationLockHeld
        } $case
        Assert-Test ((Get-FileHash -LiteralPath "$($case.Root)/comparison.zip").Hash -cne $first) 'Independent export has different ZIP timestamps'
        Invoke-Fixture $case
        Assert-Test ($case.Fake.Builds -eq 1 -and $case.Fake.Exports -eq 2) 'Retry did not rebuild signed archive or bootstrap'
        Assert-Test ($case.Fake.Prepares -eq 2 -and $case.Fake.Completions -eq 1) 'Retry reused remote immutable assets'
        Assert-Test ((Get-FileHash -LiteralPath "$($case.Prepared)/KESAS-Client-Bootstrap.zip").Hash -ceq $first) 'Prepared bootstrap unchanged'
        Assert-Test ($case.Fake.Plans -eq 2 -and $case.Fake.Verifications -ge 4) 'Retries reverified signature and bootstrap inventory'
        Assert-Test ((Get-LiveVersion $case) -ceq 'kesas-rc22' -and !(Test-Path -LiteralPath $case.Journal)) 'Retry completed native and GitHub publication'
    }
    foreach($field in @('notes','packOrigin','pin','publicKey','releaseZip')){
        Test-Case "fingerprint mismatch rejects $field without remote mutation" {
            $case=New-Fixture;$case.Fake.FailPrepareCount=1
            Assert-Throws {Invoke-Fixture $case} 'prepare unknown outcome'
            $notes='fixture notes'
            switch($field){
                'notes'{$notes='changed notes'}
                'packOrigin'{
                    $config=[IO.File]::ReadAllText("$($case.Root)/deployment.json")|ConvertFrom-Json
                    $config.address='127.0.0.2';$config.packOrigin='https://127.0.0.2:18866';Write-TestJson "$($case.Root)/deployment.json" $config
                    $feed=[IO.File]::ReadAllText("$($case.Root)/Updates/APFClientUpdate.json")|ConvertFrom-Json
                    $feed.packageUrl='https://127.0.0.2:18866/updates/kesas-rc21/APFClient-kesas-rc21.zip';Write-TestJson "$($case.Root)/Updates/APFClientUpdate.json" $feed
                }
                'pin'{
                    $config=[IO.File]::ReadAllText("$($case.Root)/deployment.json")|ConvertFrom-Json
                    $config.certificateSha256='b'*64;Write-TestJson "$($case.Root)/deployment.json" $config
                }
                'publicKey'{Write-TestText "$($case.Root)/secrets/update-signing-public.xml" 'different fixture public key'}
                'releaseZip'{
                    $stream=[IO.File]::Open($case.Release,[IO.FileMode]::Append);try{$stream.WriteByte(0)}finally{$stream.Dispose()}
                }
            }
            Assert-Throws {Invoke-Fixture $case $notes} 'fingerprint mismatch'
            Assert-Test ($case.Fake.Prepares -eq 1 -and $case.Fake.Completions -eq 0 -and $case.Fake.Stops -eq 0) 'Mismatch made no remote or service mutation'
            Assert-Test ($case.Fake.Builds -eq 1 -and $case.Fake.Exports -eq 1 -and (Test-Path -LiteralPath "$($case.Prepared)/prepared.json")) 'Mismatch preserved prepared bytes'
        }
    }
    Test-Case 'unknown publish preserves active local release and durable snapshot' {
        $case=New-PendingFixture
        $record=[IO.File]::ReadAllText($case.Journal)|ConvertFrom-Json
        Assert-Test ($case.Fake.RemotePublished -and $case.Fake.MarkerSeen) 'Remote commit happened after durable completion marker'
        Assert-Test ($record.phase -ceq 'github-completion' -and $record.githubPlan.Version -ceq 'kesas-rc22' -and $record.releaseZipSha256 -ceq (Get-FileHash -LiteralPath $case.Release).Hash.ToLowerInvariant()) 'Journal retained exact plan and source SHA'
        Assert-Test ((Get-LiveVersion $case) -ceq 'kesas-rc22' -and (Test-Path -LiteralPath "$($case.Root)/Updates/kesas-rc22/APFClient-kesas-rc22.zip")) 'Native signed release retained'
        Assert-Test ($case.Fake.Stops -eq 1 -and $case.Fake.Starts -eq 1 -and $case.Fake.Running) 'No rollback shutdown after uncertain publication'
        Assert-Test (([IO.File]::ReadAllText("$($case.Root)/Server/Universe/persistent.sfs")) -ceq 'preserve universe') 'Universe retained'
        Assert-Test (Test-Path -LiteralPath "$($case.Prepared)/prepared.json") 'Prepared files retained'
    }
    Test-Case 'completion retry verifies health and finishes with zero additional stops or restarts' {
        $case=New-PendingFixture
        $before=@($case.Fake.Stops,$case.Fake.Starts,$case.Fake.Builds,$case.Fake.Exports,$case.Fake.Prepares)
        Invoke-Fixture $case
        $after=@($case.Fake.Stops,$case.Fake.Starts,$case.Fake.Builds,$case.Fake.Exports,$case.Fake.Prepares)
        Assert-Test (($before -join ',') -ceq ($after -join ',')) 'Completion-only retry did not stop, restart, build, export, or prepare'
        Assert-Test ($case.Fake.HealthChecks -eq 1 -and $case.Fake.Completions -eq 2 -and !(Test-Path -LiteralPath $case.Journal)) 'Pinned health preceded confirmed completion and journal deletion'
        Assert-Test ((Get-LiveVersion $case) -ceq 'kesas-rc22') 'Native release remained active'
    }
    foreach($guard in @('HealthFails','MissingListener','ForeignListener','RejectSignature','PackVersion','FeedBytes','PlanSnapshot')){
        Test-Case "completion retry rejects $guard without service or remote mutation" {
            $case=New-PendingFixture
            switch($guard){
                'PackVersion'{
                    $pack=[IO.File]::ReadAllText("$($case.Root)/Server/Config/APFPack.json")|ConvertFrom-Json
                    $pack.packVersion='kesas-rc21';Write-TestJson "$($case.Root)/Server/Config/APFPack.json" $pack
                }
                'FeedBytes'{[IO.File]::AppendAllText("$($case.Root)/Updates/APFClientUpdate.json","`n")}
                'PlanSnapshot'{
                    $record=[IO.File]::ReadAllText($case.Journal)|ConvertFrom-Json
                    $record.githubPlan.ReleaseAssets[0].Sha256='f'*64;Write-TestJson $case.Journal $record
                }
                default{$case.Fake[$guard]=$true}
            }
            Assert-Throws {Invoke-Fixture $case} 'health check failed|owned services and listeners|ownership mismatch|signature rejected|differs from|differs from the verified'
            Assert-Test ($case.Fake.Stops -eq 1 -and $case.Fake.Starts -eq 1 -and $case.Fake.Completions -eq 1 -and $case.Fake.Prepares -eq 1) 'Guard prevented mutation'
            Assert-Test ((Test-Path -LiteralPath $case.Journal) -and (Get-LiveVersion $case) -ceq 'kesas-rc22') 'Guard retained journal and native release'
        }
    }
    Test-Case 'different ReleaseZIP cannot resume pending completion' {
        $case=New-PendingFixture
        $stream=[IO.File]::Open($case.Release,[IO.FileMode]::Append);try{$stream.WriteByte(0)}finally{$stream.Dispose()}
        Assert-Throws {Invoke-Fixture $case} 'unrelated or interrupted update needs review'
        Assert-Test ($case.Fake.Completions -eq 1 -and $case.Fake.Stops -eq 1 -and (Test-Path -LiteralPath $case.Journal)) 'Different release stopped for review'
    }
    Test-Case 'ordinary activation failure rolls back but retains prepared assets for retry' {
        $case=New-Fixture;$case.Fake.FailActivation=$true
        Assert-Throws {Invoke-Fixture $case} 'native activation failed'
        Assert-Test ((Get-LiveVersion $case) -ceq 'kesas-rc21' -and !(Test-Path -LiteralPath "$($case.Root)/Updates/kesas-rc22")) 'Native failure rolled back before publication boundary'
        Assert-Test (!(Test-Path -LiteralPath $case.Journal) -and (Test-Path -LiteralPath "$($case.Prepared)/prepared.json")) 'Rollback cleared native journal only'
        Assert-Test ($case.Fake.Completions -eq 0 -and $case.Fake.Running) 'No GitHub completion; old services restored'
        Invoke-Fixture $case
        Assert-Test ($case.Fake.Builds -eq 1 -and $case.Fake.Exports -eq 1 -and $case.Fake.Completions -eq 1) 'Post-rollback retry reused exact signed bytes'
    }
    Test-Case 'completion-marker write failure cannot roll back native-ready state' {
        $case=New-Fixture;$case.Fake.FailMarkerWrite=$true
        Assert-Throws {Invoke-Fixture $case} 'completion marker write failed'
        Assert-Test ((Get-LiveVersion $case) -ceq 'kesas-rc22' -and $case.Fake.Completions -eq 0) 'Marker failure retained native state without remote publication'
        Assert-Test ($case.Fake.Stops -eq 1 -and $case.Fake.Starts -eq 1 -and (Test-Path -LiteralPath $case.Journal)) 'No rollback; original journal retained'
        $case.Fake.FailMarkerWrite=$false
        Assert-Throws {Invoke-Fixture $case} 'unrelated or interrupted update needs review'
        Assert-Test ($case.Fake.Completions -eq 0) 'Non-completion journal requires review'
    }
    Test-Case 'prepared byte tampering fails closed before another preparation' {
        $case=New-Fixture;$case.Fake.FailPrepareCount=1
        Assert-Throws {Invoke-Fixture $case} 'prepare unknown outcome'
        [IO.File]::AppendAllText("$($case.Prepared)/KESAS-Client-Bootstrap.zip",'tampered')
        Assert-Throws {Invoke-Fixture $case} 'Prepared release bytes changed'
        Assert-Test ($case.Fake.Prepares -eq 1 -and $case.Fake.Stops -eq 0 -and (Test-Path -LiteralPath $case.Prepared)) 'Tampered files retained without mutation'
    }
}catch{
    $script:Failures.Add("Harness error: $($_.Exception.Message)")
}finally{
    foreach($command in $script:ProbeCommands){Remove-Item -LiteralPath ('Function:\'+$command) -ErrorAction SilentlyContinue}
    $resolved=[IO.Path]::GetFullPath($testRoot)
    $temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(!$resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -cnotmatch '^APF-GitHubRecovery-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup target'}
    if([IO.Directory]::Exists($resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
    Remove-Variable -Name PublisherRecoveryFake -Scope Global -ErrorAction SilentlyContinue
}
Write-Output "$script:Passed recovery tests passed; $($script:Failures.Count) failed."
if($script:Failures.Count){foreach($failure in $script:Failures){Write-Output $failure};throw 'GitHub recovery tests failed'}
