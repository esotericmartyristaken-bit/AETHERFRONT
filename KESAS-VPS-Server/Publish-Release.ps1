[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReleaseZip,[string]$Notes='KESAS update',[switch]$AllowActivePlayers,[string]$DeploymentRoot=$env:APF_NATIVE_ROOT,
    [switch]$LocalDeployment,[string]$LocalBotConfig,[string]$LocalNode,[string]$LocalEvidenceRoot,
    [string]$ScienceCompensationPlan='',[string]$ScienceCompensationSha256='',
    [scriptblock]$OnPublisherProgress=$null)
. "$PSScriptRoot/Update.Common.ps1"
if(-not $LocalDeployment){throw 'Git-triggered host publication is retired. Use the reviewed Deploy-LocalRelease.ps1 operator command.'}
if($AllowActivePlayers){throw 'External active-player override is disabled. Local admission must deliver the fifteen-minute warning.'}
if(-not $LocalBotConfig -or -not $LocalNode -or -not $LocalEvidenceRoot){throw 'Local deployment configuration and private evidence root are required'}

function Write-PublisherStage([string]$Stage) {
    Write-Information -MessageData $Stage -Tags 'Aetherfront-PublisherStage' -InformationAction Continue
    if($OnPublisherProgress){try{& $OnPublisherProgress $Stage}catch{Write-Warning 'Publisher phase-report callback failed; activation checks remain enforced'}}
}

function Get-PublisherAccountResetIntent([string]$Text,[string]$Version) {
    if([Text.Encoding]::UTF8.GetByteCount($Text) -gt 4096){throw 'Account reset manifest exceeds its limit'}
    Assert-ReleaseJsonUnique $Text
    $document=[Text.Json.JsonDocument]::Parse($Text)
    try{
        $value=$document.RootElement
        $fields=@('schemaVersion','applicationVersion','playerName','expectedAccountId','preserveAccountData')
        if($value.ValueKind -ne 'Object'){throw 'Invalid account reset schema'}
        $names=@($value.EnumerateObject()|ForEach-Object Name)
        if($names.Count -ne 5 -or @($names|Where-Object {$_ -cnotin $fields}).Count){throw 'Invalid account reset schema'}
        if($value.GetProperty('schemaVersion').GetRawText() -cne '1' -or
            $value.GetProperty('applicationVersion').ValueKind -ne 'String' -or
            $value.GetProperty('applicationVersion').GetString() -cne $Version -or $Version -cnotmatch '^kesas-rc[1-9][0-9]{0,14}$' -or
            $value.GetProperty('playerName').ValueKind -ne 'String' -or $value.GetProperty('playerName').GetString() -cnotmatch '^[A-Za-z0-9_-]{1,64}$' -or
            $value.GetProperty('expectedAccountId').ValueKind -ne 'String' -or $value.GetProperty('expectedAccountId').GetString() -cnotmatch '^key-[a-f0-9]{64}$' -or
            $value.GetProperty('preserveAccountData').ValueKind -ne 'True'){throw 'Invalid account reset intent'}
        return [pscustomobject]@{playerName=$value.GetProperty('playerName').GetString();expectedAccountId=$value.GetProperty('expectedAccountId').GetString()}
    }finally{$document.Dispose()}
}

function Initialize-PublisherAccountReset {
    if('ApfAccountResetPlan' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class ApfAccountResetPlan
{
    static void Need(bool condition) { if (!condition) throw new InvalidDataException("Account reset precondition failed; no reset written"); }
    static JsonObject Object(JsonNode node) { Need(node is JsonObject); return (JsonObject)node; }
    static JsonNode Field(JsonObject node, string name) { Need(node.ContainsKey(name) && node[name] != null); return node[name]; }
    static JsonArray Array(JsonObject node, string name) { var value = Field(node, name); Need(value is JsonArray); return (JsonArray)value; }
    static string Text(JsonNode node) { Need(node != null && node.GetValueKind() == JsonValueKind.String); return node.GetValue<string>(); }
    static bool Equal(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    static long Integer(JsonNode node) { Need(node != null && node.GetValueKind() == JsonValueKind.Number); long value; Need(Int64.TryParse(node.ToJsonString(), out value)); return value; }
    static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) { Need(names.Add(property.Name)); Unique(property.Value); }
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    static JsonObject Read(string text)
    {
        using (var document = JsonDocument.Parse(text)) Unique(document.RootElement);
        return Object(JsonNode.Parse(text));
    }
    static void Fields(JsonObject value, params string[] fields) { Need(value.Count == fields.Length && fields.All(value.ContainsKey)); }
    static JsonArray Bindings(JsonObject root)
    {
        Fields(root, "version", "accounts"); Need(Integer(Field(root, "version")) == 1);
        var accounts = Array(root, "accounts"); Need(accounts.Count <= 10000);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in accounts) {
            var account = Object(item); Fields(account, "accountId", "publicKey", "playerNames");
            string id = Text(Field(account, "accountId")), key = Text(Field(account, "publicKey"));
            Need(!String.IsNullOrWhiteSpace(id) && id.Length <= 256 && ids.Add(id) && !String.IsNullOrWhiteSpace(key));
            foreach (var value in Array(account, "playerNames")) {
                string name = Text(value); Need(!String.IsNullOrWhiteSpace(name));
                string owner; Need(!names.TryGetValue(name, out owner) || Equal(owner, id)); names[name] = id;
            }
        }
        return accounts;
    }
    static void Key(string key, string expected)
    {
        Need(key.Length <= 800); var parts = key.Split('.'); Need(parts.Length == 2);
        var modulus = Convert.FromBase64String(parts[0]); var exponent = Convert.FromBase64String(parts[1]);
        Need((modulus.Length == 256 || modulus.Length == 384 || modulus.Length == 512) && modulus[0] >= 128 && exponent.SequenceEqual(new byte[]{1,0,1}));
        using (var rsa = RSA.Create()) {
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            var parameters = rsa.ExportParameters(false);
            Need(key == Convert.ToBase64String(parameters.Modulus) + "." + Convert.ToBase64String(parameters.Exponent));
        }
        using (var hash = SHA256.Create()) Need(expected == "key-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant());
    }
    public static bool Reference(string value, string name, string id)
    {
        return value.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 ||
            Regex.IsMatch(value, @"(?<![A-Za-z0-9_-])" + Regex.Escape(name) + @"(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    static void NoReferences(JsonNode node, string name, string id)
    {
        if (node == null) return;
        if (node is JsonObject) foreach (var property in (JsonObject)node) { Need(!Reference(property.Key, name, id)); NoReferences(property.Value, name, id); }
        else if (node is JsonArray) foreach (var item in (JsonArray)node) NoReferences(item, name, id);
        else if (node.GetValueKind() == JsonValueKind.String) Need(!Reference(Text(node), name, id));
    }
    static void NoDisplayName(JsonNode node, string name)
    {
        if (node == null) return;
        if (node is JsonObject) foreach (var property in (JsonObject)node) {
            if (Equal(property.Key, "DisplayName")) Need(!Equal(Text(property.Value), name));
            NoDisplayName(property.Value, name);
        }
        else if (node is JsonArray) foreach (var item in (JsonArray)node) NoDisplayName(item, name);
    }
    public static string[] Build(string bindingText, string worldText, string enrollmentText, string name, string id)
    {
        Need(Regex.IsMatch(name, @"^[A-Za-z0-9_-]{1,64}$") && Regex.IsMatch(id, @"^key-[a-f0-9]{64}$"));
        string retired = "Retired-" + id.Substring(4, 16); Need(!Equal(name, retired));
        var bindingRoot = Read(bindingText); var bindings = Bindings(bindingRoot);
        var targets = bindings.Where(item => Equal(Text(Field(Object(item), "accountId")), id)).ToArray(); Need(targets.Length == 1);
        var target = Object(targets[0]); Need(Text(Field(target, "accountId")) == id);
        var aliases = Array(target, "playerNames"); Need(aliases.Count == 1 && Text(aliases[0]) == name);
        Key(Text(Field(target, "publicKey")), id);
        foreach (var item in bindings) foreach (var alias in Array(Object(item), "playerNames")) {
            Need(!Equal(Text(alias), retired));
            if (!System.Object.ReferenceEquals(item, target)) Need(!Equal(Text(alias), name));
        }
        if (enrollmentText != null) {
            var enrollment = Read(enrollmentText); Bindings(enrollment);
            NoReferences(enrollment, name, id); NoReferences(enrollment, retired, id);
        }
        var world = Read(worldText); var accounts = Array(world, "Accounts");
        var worldTargets = accounts.Where(item => Equal(Text(Field(Object(item), "AccountId")), id)).ToArray(); Need(worldTargets.Length == 1);
        var account = Object(worldTargets[0]); Need(Text(Field(account, "AccountId")) == id && Text(Field(account, "DisplayName")) == name);
        foreach (var item in accounts) {
            string displayName = Text(Field(Object(item), "DisplayName")); Need(!Equal(displayName, retired));
            if (!System.Object.ReferenceEquals(item, account)) Need(!Equal(displayName, name));
        }
        var factions = Array(world, "Factions"); NoDisplayName(factions, retired);
        foreach (var faction in factions) NoDisplayName(Array(Object(faction), "Members"), name);
        var personal = Object(Field(world, "PersonalProgression")); Need(Integer(Field(personal, "Version")) == 1);
        var players = Array(personal, "Players");
        var ledgerTargets = players.Where(item => Equal(Text(Field(Object(item), "AccountId")), id)).ToArray(); Need(ledgerTargets.Length == 1);
        var ledger = Object(ledgerTargets[0]); Need(Text(Field(ledger, "AccountId")) == id);
        long revision = Integer(Field(world, "Revision")); Need(revision >= 0 && revision < Int64.MaxValue);
        bindings.Remove(target); NoReferences(bindingRoot, name, id);
        // Release the name, not its assets: historical names, account IDs and ledger stay intact.
        account["DisplayName"] = JsonValue.Create(retired);
        world["Revision"] = JsonValue.Create(revision + 1);
        // Parsed JsonNode numeric tokens are retained without a double/PowerShell JSON round trip.
        return new[]{bindingRoot.ToJsonString(), world.ToJsonString()};
    }
}
'@
}

function Invoke-PublisherAccountReset([string]$Root,[string]$Text,[string]$Version,[switch]$ValidateOnly) {
    $intent=Get-PublisherAccountResetIntent $Text $Version
    $retiredName='Retired-'+$intent.expectedAccountId.Substring(4,16)
    Initialize-PublisherAccountReset
    $bindingPath=Get-UpdateChildPath $Root 'Server/Universe/IdentityBindings.json'
    $worldPath=Get-UpdateChildPath $Root 'Server/Aetherfront/state.json'
    $enrollmentPath=Get-UpdateChildPath $Root 'Server/Config/IdentityEnrollments.json'
    $bindingText=Read-UpdateText $bindingPath 16MB
    $worldText=Read-UpdateText $worldPath 32MB
    $enrollmentText=$null
    if([IO.File]::Exists($enrollmentPath)){$enrollmentText=Read-UpdateText $enrollmentPath 16MB}
    $enrollmentArgument=if($null -eq $enrollmentText){[System.Management.Automation.Language.NullString]::Value}else{$enrollmentText}
    $next=[ApfAccountResetPlan]::Build($bindingText,$worldText,$enrollmentArgument,$intent.playerName,$intent.expectedAccountId)
    [long]$inspectedBytes=0;[int]$inspectedFiles=0
    foreach($family in @('Scenarios','Crafts')){
        $assetRoot=Get-UpdateChildPath $Root ('Server/Universe/'+$family)
        Assert-UpdateTree $assetRoot
        if(-not [IO.Directory]::Exists($assetRoot)){continue}
        foreach($item in Get-ChildItem -LiteralPath $assetRoot -Recurse -Force){
            if([ApfAccountResetPlan]::Reference($item.Name,$intent.playerName,$intent.expectedAccountId) -or
                [ApfAccountResetPlan]::Reference($item.Name,$retiredName,$intent.expectedAccountId)){throw 'Account reset has scenario or craft references'}
            if($item.PSIsContainer){continue}
            $inspectedFiles++;$inspectedBytes+=$item.Length
            if($inspectedFiles -gt 20000 -or $inspectedBytes -gt 128MB){throw 'Account reset reference inspection limit exceeded'}
            $assetText=Read-UpdateText $item.FullName 16MB
            if([ApfAccountResetPlan]::Reference($assetText,$intent.playerName,$intent.expectedAccountId)){throw 'Account reset has scenario or craft references'}
        }
    }
    # Online inspection is advisory; unrelated live ticks may legitimately change Revision.
    if($ValidateOnly){return}
    # Recheck the exact snapshots before either atomic write. Caller owns the native
    # server lock, durable activation journal and full Universe/Aetherfront backups.
    if((Read-UpdateText $bindingPath 16MB) -cne $bindingText -or (Read-UpdateText $worldPath 32MB) -cne $worldText -or
        ([IO.File]::Exists($enrollmentPath) -ne ($null -ne $enrollmentText)) -or
        ($null -ne $enrollmentText -and (Read-UpdateText $enrollmentPath 16MB) -cne $enrollmentText)){throw 'Account reset snapshot changed'}
    Write-UpdateAtomic $worldPath $next[1]
    Write-UpdateAtomic $bindingPath $next[0]
}

function Add-ReleaseMedia([string]$ReleaseRoot,[string]$ConfigRoot,$Release,$Catalog,[string]$PrivateStage) {
    # Only additive, content-addressed public archives. Operator configuration,
    # existing media, state and keys are never replacement targets here.
    $pending=[Collections.Generic.List[object]]::new()
    $names=@{}
    $config=Assert-UpdatePath $ConfigRoot
    if(-not [IO.Directory]::Exists($config)){throw 'Media destination must already exist.'}
    foreach($file in @($Release.files | Where-Object {$_.path -clike 'MediaPayload/*'})){
        if($file.path -cnotmatch '^MediaPayload/(kesas-[a-z0-9-]+-([a-f0-9]{64})\.zip)$' -or $Matches[2] -cne $file.sha256){throw 'Media filename must contain its full declared SHA256.'}
        $name=$Matches[1]
        $names[$name]=$true
        $source=Assert-UpdatePath (Join-Path $ReleaseRoot $file.path)
        if(-not [IO.File]::Exists($source) -or ([IO.FileInfo]$source).Length -gt 512MB -or (Get-FileHash -LiteralPath $source).Hash -ine $file.sha256){throw 'Media source differs from the verified release.'}
        $references=@(foreach($profile in @($Catalog.full,$Catalog.potato)){foreach($package in @($profile.managedPackages)){if(([Uri]$package.archiveUrl).AbsolutePath -ceq ('/'+$name)){$package}}})
        if(!$references.Count -or @($references | Where-Object {$_.sha256 -cne $file.sha256 -or $_.sizeBytes -ne ([IO.FileInfo]$source).Length}).Count){throw 'Media must match every referring signed catalog declaration.'}
        Assert-GhpPublicZip $source
        $target=Assert-UpdatePath (Join-Path $config $name)
        if([IO.Directory]::Exists($target) -or ([IO.File]::Exists($target) -and (Get-FileHash -LiteralPath $target).Hash -ine $file.sha256)){throw 'Existing media is immutable; conflicting content will not be overwritten.'}
        $pending.Add([pscustomobject]@{source=$source;target=$target;sha256=$file.sha256;sizeBytes=([IO.FileInfo]$source).Length;move=$false})
    }
    if (@($Release.files | Where-Object path -ceq 'MediaReferences.json').Count) {
        $text=Read-UpdateText (Get-UpdateChildPath $ReleaseRoot 'MediaReferences.json') 1MB
        Assert-ReleaseJsonUnique $text
        $document=$text | ConvertFrom-Json
        # This file and ClientTemplate.zip were both hashed against RELEASE.json
        # inside the already repository-authenticated application archive.
        Assert-GhpMediaReferences $document $Catalog $Release.version
        foreach($asset in $document.assets){
            if($names.ContainsKey($asset.name)){throw 'Media cannot be both embedded and referenced.'}
            $names[$asset.name]=$true
        }
        # Private per-application directory, same volume as final Config files.
        # No bytes become public until the entire batch passes its full scan.
        Protect-UpdateSecrets $PrivateStage
        foreach($asset in $document.assets){
            $target=Get-UpdateChildPath $config $asset.name
            if([IO.Directory]::Exists($target)){throw 'Existing media is immutable; conflicting content will not be overwritten.'}
            if([IO.File]::Exists($target)){
                if(([IO.FileInfo]$target).Length -ne $asset.sizeBytes -or (Get-GhpSha256 $target) -cne $asset.sha256){throw 'Existing media is immutable; conflicting content will not be overwritten.'}
                Assert-GhpPublicZip $target -MaximumExpandedBytes 8GB
                continue
            }
            $source=Get-UpdateChildPath $PrivateStage $asset.name
            Save-GhpReferencedMedia $asset $source
            Assert-GhpPublicZip $source -MaximumExpandedBytes 8GB
            $pending.Add([pscustomobject]@{source=$source;target=$target;sha256=$asset.sha256;sizeBytes=$asset.sizeBytes;move=$true})
        }
    }
    # Validate the entire batch before its first append. New unreferenced media
    # may remain after a later publication failure; it never replaces live data.
    foreach($item in $pending){
        $null=Assert-UpdatePath $item.target
        if([IO.Directory]::Exists($item.target)){throw 'Existing media is immutable; conflicting content will not be overwritten.'}
        if([IO.File]::Exists($item.target)){
            if(([IO.FileInfo]$item.target).Length -ne $item.sizeBytes -or (Get-GhpSha256 $item.target) -cne $item.sha256){throw 'Existing media is immutable; conflicting content will not be overwritten.'}
            continue
        }
        if($item.move){
            if(([IO.FileInfo]$item.source).Length -ne $item.sizeBytes -or (Get-GhpSha256 $item.source) -cne $item.sha256){throw 'Media changed during private staging.'}
            try{[IO.File]::Move($item.source,$item.target)}catch{
                $null=Assert-UpdatePath $item.target
                if(-not [IO.File]::Exists($item.target) -or ([IO.FileInfo]$item.target).Length -ne $item.sizeBytes -or (Get-GhpSha256 $item.target) -cne $item.sha256){throw}
            }
            continue
        }
        $temporary=Assert-UpdatePath ($item.target+'.incoming-'+[Guid]::NewGuid().ToString('N'))
        try{
            [IO.File]::Copy($item.source,$temporary,$false)
            if(([IO.FileInfo]$temporary).Length -ne $item.sizeBytes -or (Get-GhpSha256 $temporary) -cne $item.sha256.ToLowerInvariant()){throw 'Media changed during staging.'}
            try{[IO.File]::Move($temporary,$item.target)}catch{
                $null=Assert-UpdatePath $item.target
                if(-not [IO.File]::Exists($item.target) -or ([IO.FileInfo]$item.target).Length -ne $item.sizeBytes -or (Get-GhpSha256 $item.target) -cne $item.sha256.ToLowerInvariant()){throw}
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

$root=Assert-UpdatePath $(if($DeploymentRoot){$DeploymentRoot}else{$PSScriptRoot})
$cfg=Get-UpdateDeployment $root
foreach($name in @('Server','PackHost','CertificateTool','Updates','Backups','.release-staging','.github-prepared','secrets','UpdateTool')){
    $inputPath=Get-UpdateChildPath $root $name
    # Historical backups and failed staging generations are not current inputs.
    # Validate their root/ancestors here, then validate each selected subtree
    # when it is actually used. Never rescan the entire accumulated history.
    if($name -notin @('Backups','.release-staging','.github-prepared')){Assert-UpdateTree $inputPath}
}
$releaseLock=New-Object IO.FileStream((Get-UpdateChildPath $root '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$stage=Get-UpdateChildPath $root ('.release-staging/'+[Guid]::NewGuid().ToString('N'))
$journal=Get-UpdateChildPath $root '.update-transaction.json'
$backup=$null;$changed=$false;$stopped=$false;$stopAttempted=$false;$started=$false;$published=$null;$journalWritten=$false
$activationLock=$null
$replacements=New-Object 'Collections.Generic.List[object]'
$states=New-Object 'Collections.Generic.List[object]'
$wasRunning=$false
$githubPlan=$null
$nativeReady=$false;$recoveryWatchdog=$null;$localNotification=$null
try{
    Write-PublisherStage 'guard-admission'
    $releasePath=Assert-UpdatePath (Resolve-Path -LiteralPath $ReleaseZip).Path
    $releaseSha=(Get-FileHash -LiteralPath $releasePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-UpdateKeyPair $root
    if([IO.File]::Exists($journal)){
        Write-PublisherStage 'github-completion'
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
        if(-not [IO.File]::Exists("$PSScriptRoot/GitHub.Publish.ps1")){throw 'GitHub publisher is not installed; preserve the completion journal.'}
        . "$PSScriptRoot/GitHub.Publish.ps1"
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
    Write-PublisherStage 'preparation-signing'
    Expand-UpdateArchive $releasePath "$stage/Release"
    $releaseText=Read-UpdateText "$stage/Release/RELEASE.json"
    Assert-ReleaseJsonUnique $releaseText
    $release=$releaseText | ConvertFrom-Json
    if(@($release.PSObject.Properties).Count -ne 2 -or @($release.PSObject.Properties | Where-Object Name -cnotin @('version','files')).Count -or $release.version -isnot [string] -or $release.files -isnot [array]){throw 'Unsupported release manifest schema'}
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
        if(@($file.PSObject.Properties).Count -ne 2 -or @($file.PSObject.Properties | Where-Object Name -cnotin @('path','sha256')).Count -or $file.path -isnot [string] -or $file.sha256 -isnot [string]){throw 'Unsupported release file schema'}
        $relative=Get-UpdateRelativePath $file.path
        if(!$declared.Add($relative)){throw 'Duplicate release manifest entry'}
        if(@('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json','MediaReferences.json','AccountReset.json') -cnotcontains $relative -and $relative -cnotmatch '^(ServerPayload|PackHostPayload|CertificateToolPayload|UpdateToolPayload)/' -and $relative -cnotmatch '^MediaPayload/kesas-[a-z0-9-]+-[a-f0-9]{64}\.zip$'){throw "Unsupported release file: $relative"}
        if($relative -match '^(ServerPayload|PackHostPayload|CertificateToolPayload|UpdateToolPayload)/(Config|Universe|Aetherfront|Logs|secrets)(/|$)'){throw 'Release contains protected server state'}
        if($file.sha256 -notmatch '^[a-fA-F0-9]{64}$'){throw "Invalid release SHA256: $relative"}
        $path=Get-UpdateChildPath "$stage/Release" $relative
        if(![IO.File]::Exists($path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256){throw "Release hash mismatch: $relative"}
    }
    foreach($required in @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')){if(!$declared.Contains($required)){throw "Missing required release file: $required"}}
    if(@(Get-ChildItem -LiteralPath "$stage/Release" -Recurse -Force -File).Count -ne $declared.Count+1){throw 'Release contains undeclared files'}
    $accountResetText=$null
    if($declared.Contains('AccountReset.json')){
        $accountResetText=Read-UpdateText "$stage/Release/AccountReset.json" 4096
        $null=Get-PublisherAccountResetIntent $accountResetText $version
    }
    Expand-UpdateArchive "$stage/Release/ClientTemplate.zip" "$stage/Client"
    $hasMediaReferences=$declared.Contains('MediaReferences.json')
    $clientCatalogText=Read-UpdateText "$stage/Client/GameData/LunaMultiplayer/Launcher/install-catalog.json" $(if($hasMediaReferences){1MB}else{16MB})
    if($hasMediaReferences){Assert-ReleaseJsonUnique $clientCatalogText}
    $clientCatalog=$clientCatalogText | ConvertFrom-Json
    $githubSettings=$clientCatalog.PSObject.Properties['gitHub']
    $githubEnabled=$githubSettings -and $githubSettings.Value.enabled
    $fingerprint=$null;$prepared=$null;$reuse=$false
    $bootstrapSource="$stage/KESAS-Client-Bootstrap.zip"
    if($null -ne $accountResetText){Invoke-PublisherAccountReset $root $accountResetText $version -ValidateOnly}
    . (Join-Path $PSScriptRoot '../Aetherfront-RC44-Work/Deployment/Invoke-ScienceCompensation.ps1')
    Invoke-ScienceCompensation $root $ScienceCompensationPlan $ScienceCompensationSha256 $version -ValidateOnly
    if($githubEnabled){
        if(-not [IO.File]::Exists("$PSScriptRoot/GitHub.Publish.ps1")){throw 'GitHub publisher is not installed; no services have been stopped.'}
        . "$PSScriptRoot/GitHub.Publish.ps1"
        $fingerprint=Get-PublisherFingerprint $root $cfg $releaseSha $Notes
        $prepared=Get-UpdateChildPath $root ".github-prepared/$version"
        if(Test-Path -LiteralPath $prepared){
            Assert-PublisherPrepared $prepared $fingerprint $version
            Copy-UpdateTree "$prepared/Published" "$stage/Published"
            $bootstrapSource="$prepared/KESAS-Client-Bootstrap.zip"
            $reuse=$true
        }
    }
    if(@($release.files | Where-Object {$_.path -clike 'MediaPayload/*' -or $_.path -ceq 'MediaReferences.json'}).Count){
        if(-not $githubEnabled){. "$PSScriptRoot/GitHub.Publish.ps1"}
        Write-PublisherStage 'media-publication'
        Add-ReleaseMedia "$stage/Release" "$root/Server/Config" $release $clientCatalog "$stage/Media"
    }
    if(-not $reuse){
        New-UpdateDirectory "$stage/Published"
        Invoke-UpdateTool $root @('--root-overlay','--source-root',"$stage/Client",'--output',"$stage/Published",'--version',$version,'--package-base-url',"$($cfg.packOrigin)/updates/$version/",'--private-key-xml',"$root/secrets/update-signing-private.xml",'--ksp-version','1.12.5','--release-notes',$Notes)
        & "$PSScriptRoot/Export-Client.ps1" -Template "$stage/Release/ClientTemplate.zip" -Output $bootstrapSource -PublicationLockHeld -DeploymentRoot $root
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
        Write-PublisherStage 'media-publication'
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
    $payloadFamilies=@{'ServerPayload/'='Server/';'PackHostPayload/'='PackHost/';'CertificateToolPayload/'='CertificateTool/';'UpdateToolPayload/'='UpdateTool/'}
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
    # The candidate boots and shuts down on isolated ports using these exact
    # prepared bytes before players are warned or production is stopped.
    $localCode=Assert-UpdatePath (Join-Path $PSScriptRoot '../Aetherfront-RC44-Work/Deployment')
    $candidateGate=Join-Path $localCode 'Test-PreparedCandidate.ps1'
    . $candidateGate
    . (Join-Path $localCode 'Assert-PreparedReceipt.ps1')
    . (Join-Path $localCode 'LocalAdmission.ps1')
    . (Join-Path $localCode 'Start-PreactivationRecovery.ps1')
    $localNotification=Start-LocalDeploymentNotification $LocalBotConfig $version
    $candidateReceipt=Join-Path $LocalEvidenceRoot ('candidate-'+[Guid]::NewGuid().ToString('N')+'.json')
    Write-PublisherStage 'candidate-preflight'
    $candidate=Invoke-PreparedCandidate -DeploymentRoot $root -ReleaseDirectory "$stage/Release" -PublishedDirectory "$stage/Published" -ExpectedVersion $version -ResultPath $candidateReceipt -OnCandidateProgress {param($phase) Write-PublisherStage ('candidate-'+$phase)}
    Assert-LocalPreparedReceipt $candidateReceipt $candidate.ReceiptSha256 $root "$stage/Release" "$stage/Published" $version $candidateGate
    Write-PublisherStage 'local-player-warning'
    $admission=Wait-LocalDeploymentAdmission $LocalNode $localCode $LocalBotConfig $LocalEvidenceRoot $version $Notes
    Assert-LocalPreparedReceipt $candidateReceipt $candidate.ReceiptSha256 $root "$stage/Release" "$stage/Published" $version $candidateGate
    $AllowActivePlayers=[bool]$admission.allowActivePlayers
    Write-PublisherStage 'guard-admission'
    $runningNow=@(Get-UpdateServiceProcesses $root $cfg).Count -gt 0
    $wasRunning=$wasRunning -or $runningNow
    if($wasRunning){
        if(-not $AllowActivePlayers){Assert-PublisherEmpty $root $cfg}
        if(-not $env:APF_CONTROL_ROOT){throw 'Protected native startup routing is required before shutdown'}
        $selection=Read-UpdateText (Join-Path $env:APF_CONTROL_ROOT 'native-cache/current-native.json')|ConvertFrom-Json
        $recoveryWatchdog=Start-PreactivationRecovery -Root $root -SelectedRoot $selection.directory -OldVersion $oldPack.packVersion -CandidateVersion $version -ExpectedGamePid $admission.serverPid
        if(-not $AllowActivePlayers){Assert-PublisherEmpty $root $cfg}
        $stopAttempted=$true
        Wait-UpdateServerStopped $root $cfg;$stopped=$true
    }
    Write-PublisherStage 'native-activation'
    $activationLock=Wait-UpdateServerStopped $root $cfg -TimeoutSeconds 30 -WaitOnly -AcquireLock
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
    if($null -ne $accountResetText){
        if(@(Get-UpdateServiceProcesses $root $cfg).Count){throw 'Account reset requires stopped native services'}
        Invoke-PublisherAccountReset $root $accountResetText $version
    }
    if($ScienceCompensationPlan){
        if(@(Get-UpdateServiceProcesses $root $cfg).Count){throw 'Science compensation requires stopped native services'}
        Invoke-ScienceCompensation $root $ScienceCompensationPlan $ScienceCompensationSha256 $version
    }
    if($wasRunning){$activationLock.Dispose();$activationLock=$null;$started=$true;Start-UpdatedServer $root $cfg}
    # The stable feed becomes visible only after every file and service is ready.
    Copy-UpdateAtomic "$published/APFClientUpdate.json" $currentFeed
    if($githubPlan){
        # Native readiness is the no-rollback boundary, including journal-write
        # failure. No remote completion is attempted without a durable marker.
        $nativeReady=$true
        $transaction.phase='github-completion'
        Write-UpdateAtomic $journal ($transaction|ConvertTo-Json -Depth 16)
        if($recoveryWatchdog){$null=Stop-PreactivationRecovery -Recovery $recoveryWatchdog -VerifiedHappyPath;$recoveryWatchdog=$null}
        Write-PublisherStage 'github-completion'
        Invoke-PublisherCompletion $githubPlan
    }
    [IO.File]::Delete($journal);$journalWritten=$false
    if($recoveryWatchdog){$null=Stop-PreactivationRecovery -Recovery $recoveryWatchdog -VerifiedHappyPath;$recoveryWatchdog=$null}
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
    if(-not $changed -and $stopAttempted -and -not $activationLock){
        try{
            $activationLock=Wait-UpdateServerStopped $root $cfg -TimeoutSeconds 300 -WaitOnly -AcquireLock
            $stopped=$true
        }catch{$recoveryErrors.Add($_.Exception.Message);$safeToRestore=$false}
    }
    if($changed){
        if($started){try{Wait-UpdateServerStopped $root $cfg}catch{$recoveryErrors.Add($_.Exception.Message);$safeToRestore=$false}}
        if($safeToRestore -and !$activationLock){
            try{$activationLock=Wait-UpdateServerStopped $root $cfg -TimeoutSeconds 30 -WaitOnly -AcquireLock}
            catch{$recoveryErrors.Add($_.Exception.Message);$safeToRestore=$false}
        }
        if($safeToRestore){
            try{if(@(Get-UpdateServiceProcesses $root $cfg).Count){throw 'Native services are still active; rollback is unsafe'}}
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
    if($localNotification){try{Stop-LocalDeploymentNotification $localNotification}catch{Write-Warning 'Local notification cleanup needs review; native journals are unchanged'}}
    if($activationLock){$activationLock.Dispose()}
    $releaseLock.Dispose()
    $resolved=Get-UpdateChildPath $root ('.release-staging/'+[IO.Path]::GetFileName($stage))
    Assert-UpdateTree $resolved
    if([IO.Directory]::Exists($resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
