#requires -Version 7.4
<##
Hermetic, dependency-free fixtures. No production paths, executables, ports,
world data, firewall mutations, tasks, elevation or UAC. Windows file sharing,
native helper compilation and process logging are real. Fixture-only private
ACLs additionally grant the unelevated current SID; production Admin/SYSTEM
ownership cannot be established by this unelevated test runner.
Native service startup, production observations, signature-tool execution and
firewall cmdlets are explicitly mocked in orchestration tests. A green fixture
run is NOT a passed production candidate or a signed-client verification.
Run: pwsh -NoProfile -File ./Test-PreparedCandidate.Tests.ps1
##>
[CmdletBinding()]
param([string]$Filter='*', [string]$ReleaseArchive='', [string]$ExpectedReleaseArchiveSha256='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows fixtures required for NTFS ACL/sharing checks' }
$gate=Join-Path $PSScriptRoot 'Test-PreparedCandidate.ps1'
$common=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../KESAS-VPS-Server/Update.Common.ps1'))
. $gate
. $common
. (Join-Path $PSScriptRoot 'Assert-PreparedReceipt.ps1')
Initialize-PCNative
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
#pragma warning disable SYSLIB0057 // Also compile on PowerShell 7.4 / .NET 8.
public sealed class PreparedCandidateTlsFixture : IDisposable {
    readonly TcpListener listener;
    readonly X509Certificate2 certificate;
    readonly Task worker;
    public int Port { get; private set; }
    public string ErrorMessage { get; private set; }
    public PreparedCandidateTlsFixture(string pfx, string body, int status) {
        // Windows Schannel needs a temporary imported key for this test server.
        // No certificate store is opened; Dispose releases the temporary key.
        certificate = new X509Certificate2(pfx, "", X509KeyStorageFlags.DefaultKeySet);
        listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        worker = Task.Run(async () => {
            try {
                using (var client = await listener.AcceptTcpClientAsync())
                using (var tls = new SslStream(client.GetStream(), false)) {
                    await tls.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, false);
                    var request = new StringBuilder(); var buffer = new byte[1];
                    while (request.Length < 8192 && !request.ToString().EndsWith("\r\n\r\n")) {
                        if (await tls.ReadAsync(buffer, 0, 1) == 0) return;
                        request.Append((char)buffer[0]);
                    }
                    var payload = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Length: " + payload.Length + "\r\nLocation: https://192.0.2.1/forbidden\r\nConnection: close\r\n\r\n");
                    await tls.WriteAsync(header, 0, header.Length); await tls.WriteAsync(payload, 0, payload.Length); await tls.FlushAsync();
                }
            } catch (Exception exception) { ErrorMessage = exception.GetBaseException().Message; }
        });
    }
    public void Dispose() {
        listener.Stop(); if (!worker.Wait(5000)) throw new IOException("TLS fixture did not exit"); certificate.Dispose();
    }
}
'@
$original=@{}
foreach ($name in @('Assert-PCPlatform','Assert-PCPrivate','New-PCPrivateDirectory','Get-PCProduction','Enable-PCIsolation','Disable-PCIsolation','Start-PCOwned','Wait-PCReady','Get-PCPorts','Assert-PCListeners','Invoke-PCHttps','Read-PCStartup','Read-PCBridge','Assert-PCProduction')) {
    $original[$name]=(Get-Item "Function:$name").ScriptBlock
}
$scratchBase=Join-Path ([IO.Path]::GetTempPath()) ('APF-prepared-fixtures-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($scratchBase)
$script:passed=0; $script:failed=0; $script:launches=@(); $script:productionStops=0; $script:ownedKills=@(); $script:lastContext=$null

function Assert-Fixture([bool]$Condition, [string]$Message='Fixture assertion failed') { if (-not $Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Action, [string]$Pattern='*') {
    $caught=$false
    try { & $Action | Out-Null } catch { $caught=$true; if ($_.Exception.Message -notlike $Pattern) { throw "Unexpected failure: $($_.Exception.Message)" } }
    if (-not $caught) { throw 'Expected failure did not occur' }
}
function Test-Fixture([string]$Name,[scriptblock]$Body) {
    if ($Name -notlike $Filter) { return }
    try { Reset-FixtureMocks; & $Body; $script:passed++; Write-Output "PASS $Name" }
    catch { $script:failed++; Write-Output "FAIL $Name : $($_.Exception.GetBaseException().Message) @ $($_.ScriptStackTrace)" }
}
function Set-FixtureMock([string]$Name,[scriptblock]$Body) { Set-Item -Path "Function:script:$Name" -Value $Body }
function Reset-FixtureMocks {
    foreach ($name in $original.Keys) { Set-FixtureMock $name $original[$name] }
    $script:launches=@(); $script:productionStops=0; $script:ownedKills=@(); $script:lastContext=$null
    Set-FixtureMock Assert-PCPlatform { } # never requests elevation
    Set-FixtureMock Assert-PCPrivate {
        param($Path)
        $acl=Get-Acl -LiteralPath $Path
        Assert-Fixture $acl.AreAccessRulesProtected 'Fixture ACL must be private'
        $current=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            Assert-Fixture ($rule.IdentityReference.Value -in @($current,'S-1-5-18','S-1-5-32-544')) 'Unexpected fixture ACL principal'
        }
    }
    Set-FixtureMock New-PCPrivateDirectory {
        param($Path)
        if (Test-Path -LiteralPath $Path) { throw 'Fixture directory must be NEW' }
        $acl=[Security.AccessControl.DirectorySecurity]::new(); $acl.SetAccessRuleProtection($true,$false)
        $current=[Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.SetOwner($current)
        foreach ($sid in @($current.Value,'S-1-5-18','S-1-5-32-544')) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        }
        [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path),$acl)
        Assert-PCPrivate $Path
        return [IO.Path]::GetFullPath($Path)
    }
    Set-FixtureMock Get-PCProduction {
        param($Context)
        [pscustomobject]@{ processes=@(@{pid=101;path='fixture-production-game'},@{pid=102;path='fixture-production-pack'});udp=@('0.0.0.0:18865/101');tcp=@('0.0.0.0:18866/102');packSha256=Get-PCHash "$($Context.Root)/Server/Config/APFPack.json" }
    }
    Set-FixtureMock Get-PCPorts { param($Context) $Context.GamePort=28865; $Context.PackPort=28866 }
    Set-FixtureMock Enable-PCIsolation { param($Context) $Context.Receipt.isolation=@{fixtureOnly=$true}; $Context.IsolationEnabled=$true }
    Set-FixtureMock Disable-PCIsolation { param($Context) if ($Context.Owned.Count) { throw 'Fixture isolation cannot be removed with live candidate' } }
    Set-FixtureMock Start-PCOwned {
        param($Context,$Role)
        Assert-Fixture ($Context.IsolationEnabled -eq $true) 'Process launched before isolation'
        $script:lastContext=$Context; $script:launches+=,$Role
        New-FixtureOwned $Context $Role
    }
    Set-FixtureMock Wait-PCReady {
        param($Context)
        # Exercise real on-disk assembly/configuration, not a synthetic receipt.
        $config="$($Context.Candidate)/Server/Config"
        $connection=[xml](Read-PCText "$config/ConnectionSettings.xml")
        Assert-Fixture ($connection.ConnectionSettingsDefinition.ListenAddress -ceq '127.0.0.1')
        Assert-Fixture ($connection.ConnectionSettingsDefinition.Upnp -ceq 'false')
        $master=[xml](Read-PCText "$config/MasterServerSettings.xml")
        Assert-Fixture ($master.MasterServerSettingsDefinition.RegisterWithMasterServer -ceq 'false')
        $website=[xml](Read-PCText "$config/WebsiteSettings.xml")
        Assert-Fixture ($website.WebsiteSettingsDefinition.EnableWebsite -ceq 'false')
        Assert-Fixture ((Read-PCText "$config/APFManifestUrl.txt").Trim() -ceq 'https://127.0.0.1:28866/APFPack.json')
        Assert-Fixture ((Read-PCText "$config/APFServer.txt").Trim() -ceq '127.0.0.1:28865')
        Assert-Fixture ((Read-PCText "$config/APFDirectServer.txt").Trim() -ceq 'true')
        Assert-Fixture ((Read-PCText "$($Context.Candidate)/Server/Server.dll") -ceq 'exact-new-server')
        Assert-Fixture ((Read-PCText "$($Context.Candidate)/Server/Universe/Scenarios/science.txt") -ceq 'fixture-science-world')
        Assert-Fixture ((Read-PCText "$($Context.Candidate)/Server/Aetherfront/state.json") -ceq '{"science":123}')
        Assert-Fixture (-not (Test-Path -LiteralPath "$config/never-copy.zip")) 'Media copied'
        Assert-Fixture (-not (Test-Path -LiteralPath "$($Context.Candidate)/secrets/update-signing-private.xml")) 'Signing private key copied'
        Assert-Fixture (-not (Test-Path -LiteralPath "$($Context.Candidate)/Server/APFDiscordBridge/inbox/old.json")) 'Live bridge queue copied'
        $pack=Read-PCText "$config/APFPack.json" | ConvertFrom-Json
        Assert-Fixture ($pack.packVersion -ceq 'kesas-rc999' -and $pack.serverId -ceq 'fixture-server' -and $pack.packId -ceq 'fixture-pack')
        Assert-Fixture ($pack.fixtureUrl -ceq 'https://127.0.0.1:28866/local.zip')
        Assert-Fixture ((Get-PCHash "$($Context.Candidate)/Updates/APFClientUpdate.json") -ceq (Get-PCHash "$($Context.Published)/APFClientUpdate.json"))
        Assert-Fixture ((Get-Item -LiteralPath "$($Context.Scratch)/Snapshots/Universe/Scenarios/science.txt").IsReadOnly)
        Assert-Fixture (-not (Get-Item -LiteralPath "$($Context.Candidate)/Server/Universe/Scenarios/science.txt").IsReadOnly)
        [IO.File]::WriteAllText("$($Context.Candidate)/Server/Aetherfront/state.json",'{"science":456}')
        $Context.Receipt.https=@{fixtureOnly=$true}
        [IO.File]::WriteAllText("$($Context.Scratch)/Logs/game.stdout.log",'All systems up and running.')
    }
}

function New-FixtureOwned($Context,[string]$Role) {
    $executable=switch ($Role) { Game {"$($Context.Candidate)/Server/Server.exe"} Pack {"$($Context.Candidate)/PackHost/ApfPackHost.exe"} Verify {"$($Context.Candidate)/UpdateTool/ApfUpdateTool.exe"} }
    $number=switch ($Role) {Game {70001} Pack {70002} Verify {70003}}
    $process=[pscustomobject]@{Id=$number;HasExited=($Role -eq 'Verify');ExitCode=0;StandardInput=$null}
    $stdin=[pscustomobject]@{Process=$process}
    $stdin | Add-Member ScriptMethod WriteLine { param($Line) Assert-Fixture ($Line -ceq '/shutdown'); $this.Process.HasExited=$true }
    $stdin | Add-Member ScriptMethod Flush { }
    $process.StandardInput=$stdin
    $process | Add-Member ScriptMethod Kill { $script:ownedKills+=,$this.Id; $this.HasExited=$true }
    $process | Add-Member ScriptMethod WaitForExit { param($Milliseconds) return $this.HasExited }
    $process | Add-Member ScriptMethod CloseMainWindow { return $false }
    $owned=[pscustomobject]@{Process=$process;Executable=$executable;StartedUtc=[DateTime]::UtcNow;OutputTask=[Threading.Tasks.Task]::CompletedTask;ErrorTask=[Threading.Tasks.Task]::CompletedTask}
    $owned | Add-Member ScriptMethod AssertOwned { }
    $owned | Add-Member ScriptMethod Drain { }
    $owned | Add-Member ScriptMethod Dispose { Assert-Fixture $this.Process.HasExited 'Disposed live fixture' }
    return $owned
}

function New-Fixture {
    $base=Join-Path $scratchBase ([Guid]::NewGuid().ToString('N')); $null=[IO.Directory]::CreateDirectory($base)
    $root="$base/Deployment"; $release="$base/Release"; $published="$base/Published"
    foreach ($directory in @("$root/Server/Config","$root/Server/Universe/Scenarios","$root/Server/Aetherfront","$root/Server/Plugins/APFDiscordBridge","$root/Server/APFDiscordBridge/inbox","$root/Server/logs","$root/PackHost","$root/CertificateTool","$root/UpdateTool","$root/secrets","$release/ServerPayload",$published)) { $null=[IO.Directory]::CreateDirectory($directory) }
    foreach ($entry in @{
        'Server/Server.exe'='fixture-native-not-executable';'Server/Server.dll'='old-server';'Server/Server.runtimeconfig.json'='{}';
        'Server/Plugins/APFDiscordBridge/APFDiscordBridge.dll'='fixture-bridge';'PackHost/ApfPackHost.exe'='fixture-pack';
        'CertificateTool/ApfCertificateTool.exe'='fixture-certificate-tool';'UpdateTool/ApfUpdateTool.exe'='fixture-update-tool';
        'secrets/update-signing-public.xml'='<RSAKeyValue />';'secrets/update-signing-private.xml'='DO-NOT-COPY';
        'Server/Universe/Scenarios/science.txt'='fixture-science-world';'Server/Aetherfront/state.json'='{"science":123}';
        'Server/APFDiscordBridge/inbox/old.json'='{"command":"shutdown"}';'Server/logs/live.log'='DO-NOT-COPY';
        'Server/Config/never-copy.zip'='DO-NOT-COPY-MEDIA';'Server/Config/AetherfrontTechnology.json'='{}';
        'Server/Config/APFPack.json'='{"serverId":"fixture-server","packId":"fixture-pack","packVersion":"kesas-rc998","requiredClientVersion":"kesas-rc998"}';
        'Server/Config/ConnectionSettings.xml'='<ConnectionSettingsDefinition><ListenAddress>0.0.0.0</ListenAddress><Port>18865</Port><Upnp>true</Upnp></ConnectionSettingsDefinition>';
        'Server/Config/GeneralSettings.xml'='<GeneralSettingsDefinition><GameMode>Science</GameMode><MaxPlayers>8</MaxPlayers></GeneralSettingsDefinition>'
    }.GetEnumerator()) { [IO.File]::WriteAllText("$root/$($entry.Key)",$entry.Value) }
    [IO.File]::Copy($common,"$root/Update.Common.ps1")
    $rsa=[Security.Cryptography.RSA]::Create(2048)
    try {
        $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=fixture',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddHours(1))
        try { [IO.File]::WriteAllBytes("$root/secrets/pack.pfx",$certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,'')); $pin=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData)).ToLowerInvariant() }
        finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
    Write-PCJson "$root/deployment.json" @{address='192.0.2.10';gamePort=18865;packPort=18866;packOrigin='https://192.0.2.10:18866';certificateSha256=$pin}
    foreach ($entry in @{
        'ServerPayload/Server.dll'='exact-new-server';'ClientTemplate.zip'='fixture-not-a-client-zip';'AetherfrontTechnology.json'='{"fixture":true}';
        'APFPack-template.json'='{"serverId":"ignored","packId":"ignored","packVersion":"kesas-rc999","requiredClientVersion":"kesas-rc999","fixtureUrl":"https://192.0.2.10:18866/local.zip"}'
    }.GetEnumerator()) { [IO.File]::WriteAllText("$release/$($entry.Key)",$entry.Value) }
    $files=@(Get-PCFiles $release | ForEach-Object { @{path=[IO.Path]::GetRelativePath($release,$_).Replace('\','/');sha256=Get-PCHash $_} })
    Write-PCJson "$release/RELEASE.json" @{version='kesas-rc999';files=$files}
    [IO.File]::WriteAllText("$published/APFClient-kesas-rc999.zip",'fixture-signed-bytes-not-a-real-signed-zip')
    Write-PCJson "$published/APFClientUpdate.json" @{version='kesas-rc999';packageUrl='https://192.0.2.10:18866/updates/kesas-rc999/APFClient-kesas-rc999.zip';sha256=Get-PCHash "$published/APFClient-kesas-rc999.zip";sizeBytes=(Get-Item "$published/APFClient-kesas-rc999.zip").Length;files=@(@{path='fixture';sha256=('a'*64)});signature='fixture-signature-NOT-verified'}
    [pscustomobject]@{Base=$base;Root=$root;Release=$release;Published=$published;Result="$base/Evidence/result.json"}
}
function Invoke-Fixture($Fixture) {
    try { Invoke-PreparedCandidate -DeploymentRoot $Fixture.Root -ReleaseDirectory $Fixture.Release -PublishedDirectory $Fixture.Published -ExpectedVersion 'kesas-rc999' -ResultPath $Fixture.Result }
    catch {
        if (Test-Path -LiteralPath $Fixture.Result) { $r=Read-FixtureReceipt $Fixture; throw "$($_.Exception.Message) [fixture detail: $($r.failureDetail)]" }
        throw
    }
}
function Read-FixtureReceipt($Fixture) { Read-PCText $Fixture.Result | ConvertFrom-Json }

# Production-control sentinels. A forbidden route always fails the fixture.
function Wait-UpdateServerStopped { $script:productionStops++; throw 'Forbidden production stop route' }
function Start-UpdatedServer { throw 'Forbidden production start route' }
function Stop-Process { $script:productionStops++; throw 'Forbidden global Stop-Process route' }
function Start-ScheduledTask { throw 'Forbidden scheduled-task route' }

try {
    Test-Fixture 'helpers parse and compile; entry point restricted to five parameters' {
        $tokens=$null; $errors=$null; $ast=[Management.Automation.Language.Parser]::ParseFile($gate,[ref]$tokens,[ref]$errors)
        Assert-Fixture ($errors.Count -eq 0)
        Assert-PCSame @('DeploymentRoot','ReleaseDirectory','PublishedDirectory','ExpectedVersion','ResultPath') @($ast.ParamBlock.Parameters | ForEach-Object {$_.Name.VariablePath.UserPath}) 'Unexpected execution parameter'
        $source=Read-PCText $gate
        Assert-Fixture ($source -notmatch 'Start-ScheduledTask|Stop-Process|Wait-UpdateServerStopped|Start-UpdatedServer|Start-Server\.ps1|Stop-Server\.ps1') 'Production route present in gate'
    }
    Test-Fixture 'fixture-only private ACL and disjoint NEW result required' {
        $f=New-Fixture; $private=New-PCPrivateDirectory "$($f.Base)/Private"; Assert-PCPrivate $private
        $f.Result="$($f.Root)/result.json"; Expect-Failure { Invoke-Fixture $f }
        Assert-Fixture ($script:launches.Count -eq 0 -and $script:productionStops -eq 0)
    }
    Test-Fixture 'payload hash failure never starts/stops anything' {
        $f=New-Fixture; [IO.File]::WriteAllText("$($f.Release)/ServerPayload/Server.dll",'corrupt')
        Expect-Failure { Invoke-Fixture $f } '*gate failed*'
        $r=Read-FixtureReceipt $f
        Assert-Fixture (-not $r.success -and -not $r.startPassed -and $r.failureStage -ceq 'inputs')
        Assert-Fixture ($script:launches.Count -eq 0 -and $script:productionStops -eq 0)
    }
    Test-Fixture 'protected native payload rejected even with matching hash' {
        $f=New-Fixture; $null=[IO.Directory]::CreateDirectory("$($f.Release)/ServerPayload/Universe")
        $path="$($f.Release)/ServerPayload/Universe/world.json"; [IO.File]::WriteAllText($path,'{}')
        $manifest=Read-PCText "$($f.Release)/RELEASE.json" | ConvertFrom-Json
        $manifest.files+=@{path='ServerPayload/Universe/world.json';sha256=Get-PCHash $path}; Write-PCJson "$($f.Release)/RELEASE.json" $manifest
        Expect-Failure { Invoke-Fixture $f } '*gate failed*'; Assert-Fixture ($script:launches.Count -eq 0 -and $script:productionStops -eq 0)
    }
    Test-Fixture 'exact LunaCompat LICENSE is accepted and hash-bound; other notices remain rejected' {
        $f=New-Fixture
        $manifest=Read-PCText "$($f.Release)/RELEASE.json" | ConvertFrom-Json
        $relative='ServerPayload/Plugins/LunaCompat/LICENSE.txt'
        $path=Get-UpdateChildPath $f.Release $relative
        $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllText($path,'fixture exact-path license')
        $manifest.files+=@{path=$relative;sha256=Get-PCHash $path}
        Write-PCJson "$($f.Release)/RELEASE.json" $manifest
        $answer=Invoke-Fixture $f; $receipt=Read-FixtureReceipt $f
        Assert-Fixture ($answer.Success -and $receipt.success -and $receipt.releasePayloadHashes.Count -eq 2)
        $target="$($script:lastContext.Candidate)/Server/Plugins/LunaCompat/LICENSE.txt"
        Assert-Fixture ((Get-PCHash $target) -ceq (Get-PCHash $path))
        Assert-Fixture (@($receipt.candidateFiles | Where-Object path -CEQ 'Server/Plugins/LunaCompat/LICENSE.txt').Count -eq 1)
        foreach ($forbidden in @('ServerPayload/Plugins/Other/LICENSE.txt','ServerPayload/Plugins/LunaCompat/NOTICE.txt','ServerPayload/Plugins/LunaCompat/runtime-output.txt','ServerPayload/Plugins/LunaCompat/execute.ps1','ServerPayload/Universe/LICENSE.txt','ServerPayload/secrets/NOTICE.md')) {
            $forbiddenPath=Get-UpdateChildPath $f.Release $forbidden
            $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($forbiddenPath))
            [IO.File]::WriteAllText($forbiddenPath,'fixture not permitted')
            Write-PCJson "$($f.Release)/RELEASE.json" @{version='kesas-rc999';files=@($manifest.files)+@(@{path=$forbidden;sha256=Get-PCHash $forbiddenPath})}
            Expect-Failure { Get-PCRelease $f.Release 'kesas-rc999' } '*payload*'
            [IO.File]::Delete($forbiddenPath)
        }
        Write-PCJson "$($f.Release)/RELEASE.json" $manifest
        [IO.File]::AppendAllText($path,' tampered')
        Expect-Failure { Get-PCRelease $f.Release 'kesas-rc999' } '*payload hash mismatch*'
        Assert-Fixture ($script:productionStops -eq 0)
    }
    if ($ReleaseArchive) {
        Test-Fixture 'exact supplied release archive inventory passes without native execution' {
            if ($ExpectedReleaseArchiveSha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Supply exact ExpectedReleaseArchiveSha256 with ReleaseArchive' }
            Assert-Fixture ((Get-PCHash $ReleaseArchive) -ceq $ExpectedReleaseArchiveSha256) 'Supplied release archive SHA mismatch'
            $destination=Join-Path $scratchBase ('native-inventory-'+[Guid]::NewGuid().ToString('N'))
            Expand-UpdateArchive $ReleaseArchive $destination
            $version=(Read-PCText "$destination/RELEASE.json" | ConvertFrom-Json).version
            $release=Get-PCRelease $destination $version
            Assert-Fixture ($release.version -ceq $version)
            Assert-Fixture (@($release.files | Where-Object path -CEQ 'ServerPayload/Plugins/LunaCompat/LICENSE.txt').Count -eq 1) 'Native LunaCompat notice regression not exercised'
            Assert-Fixture ($script:launches.Count -eq 0 -and $script:productionStops -eq 0)
            Assert-Fixture ((Get-PCHash $ReleaseArchive) -ceq $ExpectedReleaseArchiveSha256) 'Input archive changed'
        }
    }
    Test-Fixture 'changed prepared ZIP rejected before executable launch' {
        $f=New-Fixture; [IO.File]::AppendAllText("$($f.Published)/APFClient-kesas-rc999.zip",'tamper')
        Expect-Failure { Invoke-Fixture $f } '*gate failed*'; Assert-Fixture ($script:launches.Count -eq 0)
    }
    Test-Fixture 'failed isolation never launches executable' {
        $f=New-Fixture; Set-FixtureMock Enable-PCIsolation { param($Context) throw 'fixture isolation failure' }
        Expect-Failure { Invoke-Fixture $f } '*gate failed*'
        Assert-Fixture ((Read-FixtureReceipt $f).failureStage -ceq 'isolation')
        Assert-Fixture ($script:launches.Count -eq 0 -and $script:productionStops -eq 0)
    }
    Test-Fixture 'signature-tool rejection never launches game/pack' {
        $f=New-Fixture
        Set-FixtureMock Start-PCOwned { param($Context,$Role) $script:launches+=,$Role; $owned=New-FixtureOwned $Context $Role; $owned.Process.ExitCode=2; return $owned }
        Expect-Failure { Invoke-Fixture $f } '*gate failed*'
        Assert-PCSame @('Verify') $script:launches 'Game/pack launched after rejected signature'
        Assert-Fixture ((Read-FixtureReceipt $f).failureStage -ceq 'signature')
    }
    Test-Fixture 'game start failure stops only owned pack, never production' {
        $f=New-Fixture
        Set-FixtureMock Start-PCOwned { param($Context,$Role) $script:lastContext=$Context; $script:launches+=,$Role; if ($Role -eq 'Game') { throw 'fixture native startup failure' }; return (New-FixtureOwned $Context $Role) }
        Expect-Failure { Invoke-Fixture $f } '*gate failed*'
        $r=Read-FixtureReceipt $f
        Assert-Fixture (-not $r.success -and $r.shutdownPassed -and $r.productionUnchanged -and $r.failureStage -ceq 'startup')
        Assert-PCSame @(70002) $script:ownedKills 'Unexpected process terminated'
        Assert-Fixture ($script:productionStops -eq 0 -and $script:lastContext.Owned.Count -eq 0)
    }
    Test-Fixture 'mocked native success binds exact inputs, snapshot, shutdown; no production route' {
        $f=New-Fixture; $before=Get-PCHash "$($f.Root)/Server/Aetherfront/state.json"
        $answer=Invoke-Fixture $f; $r=Read-FixtureReceipt $f
        Assert-Fixture ($answer.Success -and $r.success -and $r.startPassed -and $r.shutdownPassed -and $r.productionUnchanged -and $r.productionPidUnchanged)
        Assert-Fixture ($r.releaseManifestSha256 -ceq (Get-PCHash "$($f.Release)/RELEASE.json"))
        Assert-Fixture ($r.gateSha256 -ceq (Get-PCHash $gate))
        Assert-Fixture ($r.publishedFiles.Count -eq 2 -and $r.liveBaselineHashes.Count -gt 8 -and $r.baselineChecks.Count -ge 3)
        Assert-Fixture ($r.version -ceq 'kesas-rc999' -and $r.createdAt -ceq $r.finishedAt)
        Assert-Fixture ($r.releasePayloadHashes.Count -eq 1 -and $r.releasePayloadHashes[0].path -ceq 'ServerPayload/Server.dll')
        Assert-Fixture ($r.signedFeedSha256 -ceq (Get-PCHash "$($f.Published)/APFClientUpdate.json") -and $r.signedArchiveSha256 -ceq (Get-PCHash "$($f.Published)/APFClient-kesas-rc999.zip"))
        Assert-Fixture ($r.startedAt -le $r.finishedAt -and $r.shutdown.passed -and $r.worldSnapshot.Count -eq 2)
        Assert-Fixture ((Get-PCHash "$($f.Root)/Server/Aetherfront/state.json") -ceq $before)
        Assert-PCPrivate ([IO.Path]::GetDirectoryName($f.Result))
        Assert-Fixture ($script:productionStops -eq 0)
        $existing=Get-PCHash $f.Result; Expect-Failure { Invoke-Fixture $f } '*NEW JSON receipt*'; Assert-Fixture ((Get-PCHash $f.Result) -ceq $existing)
    }
    Test-Fixture 'receipt accepts equivalent Windows path spellings without weakening proof' {
        $f=New-Fixture; $answer=Invoke-Fixture $f
        Assert-LocalPreparedReceipt $f.Result $answer.ReceiptSha256 ($f.Root.Replace('\','/')+'/.') ($f.Release.Replace('\','/')+'/.') ($f.Published.Replace('\','/')+'/.') 'kesas-rc999' $gate
        Assert-Fixture ($script:productionStops -eq 0)
    }
    Test-Fixture 'receipt still rejects changed identity flags gate expiry and bytes' {
        $f=New-Fixture; $answer=Invoke-Fixture $f; $original=Read-PCText $f.Result
        foreach($change in @(
            @{field='deploymentRoot';value=$f.Release},@{field='releaseDirectory';value=$f.Root},@{field='publishedDirectory';value=$f.Release},
            @{field='version';value='kesas-rc998'},@{field='schema';value='wrong'},@{field='gateSha256';value=('0'*64)},
            @{field='success';value=$false},@{field='startPassed';value=$false},@{field='shutdownPassed';value=$false},
            @{field='productionUnchanged';value=$false},@{field='productionPidUnchanged';value=$false},
            @{field='createdAt';value=([DateTime]::UtcNow.AddHours(-2).ToString('o'))}
        )){
            $changed=$original|ConvertFrom-Json; $changed.($change.field)=$change.value; Write-PCJson $f.Result $changed
            Expect-Failure { Assert-LocalPreparedReceipt $f.Result (Get-PCHash $f.Result) $f.Root $f.Release $f.Published 'kesas-rc999' $gate } '*proof*'
        }
        [IO.File]::WriteAllText($f.Result,$original)
        Expect-Failure { Assert-LocalPreparedReceipt $f.Result ('0'*64) $f.Root $f.Release $f.Published 'kesas-rc999' $gate } '*receipt changed*'
        Assert-Fixture ($script:productionStops -eq 0)
    }
    Test-Fixture 'production static drift invalidates otherwise passing candidate' {
        $f=New-Fixture
        Set-FixtureMock Wait-PCReady { param($Context) [IO.File]::AppendAllText("$($Context.Root)/Server/Server.dll",'fixture drift') }
        Expect-Failure { Invoke-Fixture $f } '*gate failed*'
        $r=Read-FixtureReceipt $f; Assert-Fixture (-not $r.success -and -not $r.productionUnchanged -and $script:productionStops -eq 0)
    }
    Test-Fixture 'cleanup refuses executable outside candidate even when injected into owned list' {
        $f=New-Fixture
        $context=@{Candidate="$($f.Base)/Candidate";Receipt=@{};Owned=[Collections.Generic.List[object]]::new();Game=$null}
        $owned=New-FixtureOwned $context Pack; $owned.Executable="$($f.Root)/PackHost/ApfPackHost.exe"; $context.Owned.Add($owned)
        Expect-Failure { Stop-PCOwned $context } '*shutdown failed*'
        Assert-Fixture (-not $owned.Process.HasExited -and $script:ownedKills.Count -eq 0 -and -not $context.Receipt.shutdown.passed)
    }
    Test-Fixture 'broken stdin still terminates only verified owned survivors and unregisters exits' {
        $f=New-Fixture; $script:fixtureDisposed=0
        $context=@{Candidate="$($f.Base)/Candidate";Receipt=@{startPassed=$false};Owned=[Collections.Generic.List[object]]::new();Game=$null}
        $game=New-FixtureOwned $context Game; $pack=New-FixtureOwned $context Pack
        $game.Process.StandardInput | Add-Member ScriptMethod WriteLine { param($Line) throw 'fixture broken stdin' } -Force
        $game | Add-Member ScriptMethod Dispose { Assert-Fixture $this.Process.HasExited; $script:fixtureDisposed++ } -Force
        $context.Game=$game; $context.Owned.Add($pack); $context.Owned.Add($game)
        Expect-Failure { Stop-PCOwned $context } '*clean shutdown failed*'
        Assert-PCSame @(70001,70002) $script:ownedKills 'Broken stdin skipped verified owned termination'
        Assert-Fixture ($context.Owned.Count -eq 0 -and $script:fixtureDisposed -eq 1 -and $script:productionStops -eq 0)
        Assert-Fixture (-not $context.Receipt.shutdown.passed -and $game.Process.HasExited -and $pack.Process.HasExited)
        Disable-PCIsolation $context
    }
    Test-Fixture 'faulted log drain still disposes/unregisters confirmed exit and permits firewall cleanup' {
        $f=New-Fixture; $script:fixtureDisposed=0
        $context=@{Candidate="$($f.Base)/Candidate";Receipt=@{startPassed=$true};Owned=[Collections.Generic.List[object]]::new();Game=$null}
        $game=New-FixtureOwned $context Game
        $game | Add-Member ScriptMethod Drain { throw 'fixture faulted stdout drain' } -Force
        $game | Add-Member ScriptMethod Dispose { Assert-Fixture $this.Process.HasExited; $script:fixtureDisposed++ } -Force
        $context.Game=$game; $context.Owned.Add($game)
        Expect-Failure { Stop-PCOwned $context } '*clean shutdown failed*'
        Assert-Fixture ($game.Process.HasExited -and $context.Owned.Count -eq 0 -and $script:fixtureDisposed -eq 1)
        Assert-Fixture ($script:ownedKills.Count -eq 0 -and $script:productionStops -eq 0 -and -not $context.Receipt.shutdown.passed)
        Assert-Fixture (-not $context.Receipt.shutdown.processes[0].loggingPassed -and $context.Receipt.shutdown.processes[0].disposed)
        Disable-PCIsolation $context
    }
    Test-Fixture 'shared read copy tolerates live read-write-delete handle; no hardlink' {
        $f=New-Fixture; $source="$($f.Base)/shared.txt"; $destination="$($f.Base)/copy.txt"
        [IO.File]::WriteAllText($source,'source-content')
        $held=[IO.File]::Open($source,'Open','ReadWrite',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try { $copy=Copy-PCFile $source $destination; Assert-Fixture ($copy.sha256 -ceq (Get-PCHash $source)) }
        finally { $held.Dispose() }
        [IO.File]::WriteAllText($destination,'candidate-only')
        Assert-Fixture ((Read-PCText $source) -ceq 'source-content')
    }
    Test-Fixture 'nonloopback/wrong owner candidate listener fails closed' {
        $context=@{Game=@{Process=@{Id=70001}};Pack=@{Process=@{Id=70002}};GamePort=28865;PackPort=28866}
        function Get-NetUDPEndpoint { [pscustomobject]@{OwningProcess=70001;LocalAddress='0.0.0.0';LocalPort=28865} }
        function Get-NetTCPConnection { @() }
        Expect-Failure { Assert-PCListeners $context -Require } '*UDP*'
        function Get-NetUDPEndpoint { [pscustomobject]@{OwningProcess=101;LocalAddress='127.0.0.1';LocalPort=28865} }
        Expect-Failure { Assert-PCListeners $context -Require } '*UDP*'
    }
    Test-Fixture 'owned auxiliary UDP must settle before readiness; main and foreign listeners stay fatal' {
        $context=@{Game=@{Process=@{Id=70001}};Pack=@{Process=@{Id=70002}};GamePort=28865;PackPort=28866;Receipt=@{}}
        $script:auxiliaryUdp=$true
        function Get-NetUDPEndpoint {
            [pscustomobject]@{OwningProcess=70001;LocalAddress='127.0.0.1';LocalPort=28865}
            if($script:auxiliaryUdp){[pscustomobject]@{OwningProcess=70001;LocalAddress='0.0.0.0';LocalPort=55123}}
        }
        function Get-NetTCPConnection {
            [CmdletBinding()]param($State)
            if($State -eq 'Listen'){[pscustomobject]@{OwningProcess=70002;LocalAddress='127.0.0.1';LocalPort=28866}}
        }
        Assert-Fixture (-not (Assert-PCListeners $context))
        Assert-Fixture ($context.Receipt.lastPendingUdp.port -eq 55123)
        Expect-Failure {Assert-PCListeners $context -Require} '*auxiliary UDP*'
        $script:auxiliaryUdp=$false
        Assert-Fixture (Assert-PCListeners $context -Require)
    }
    Test-Fixture 'accepted loopback HTTPS ephemeral remote ports including CloseWait are inbound' {
        $context=@{Game=@{Process=@{Id=70001}};Pack=@{Process=@{Id=70002}};GamePort=28865;PackPort=28866}
        $script:fixtureForbiddenOutbound=$false
        function Get-NetUDPEndpoint { [pscustomobject]@{OwningProcess=70001;LocalAddress='127.0.0.1';LocalPort=28865} }
        function Get-NetTCPConnection {
            [CmdletBinding()]param($State)
            $listener=[pscustomobject]@{OwningProcess=70002;LocalAddress='127.0.0.1';LocalPort=28866;RemoteAddress='0.0.0.0';RemotePort=0;State='Listen'}
            if ($State -eq 'Listen') { return $listener }
            $listener
            foreach ($connectionState in @('Established','CloseWait')) {
                [pscustomobject]@{OwningProcess=70002;LocalAddress='127.0.0.1';LocalPort=28866;RemoteAddress='127.0.0.1';RemotePort=57123;State=$connectionState}
            }
            if ($script:fixtureForbiddenOutbound) {
                [pscustomobject]@{OwningProcess=70002;LocalAddress='127.0.0.1';LocalPort=57124;RemoteAddress='127.0.0.1';RemotePort=18866;State='Established'}
            }
        }
        Assert-Fixture (Assert-PCListeners $context -Require)
        $script:fixtureForbiddenOutbound=$true
        Expect-Failure { Assert-PCListeners $context -Require } '*forbidden outbound*'
    }
    Test-Fixture 'firewall plan blocks nonloopback and other loopback ports for candidate executables only' {
        $f=New-Fixture; $script:firewallSpecifications=@{}
        $context=@{Candidate=($f.Root+'/.');Root="$($f.Base)/Live-not-candidate";GamePort=28865;PackPort=28866;Receipt=@{};Firewall=[Collections.Generic.List[string]]::new()}
        function Get-NetFirewallProfile { @([pscustomobject]@{Enabled=$true},[pscustomobject]@{Enabled=$true},[pscustomobject]@{Enabled=$true}) }
        function New-NetFirewallRule {
            [CmdletBinding()]param($Name,$DisplayName,$Program,$Direction,$Action,$Enabled,$Profile,$Protocol,$RemoteAddress,$RemotePort)
            $script:firewallSpecifications[$Name]=[pscustomobject]@{Name=$Name;Program=$Program;Direction=$Direction;Action=$Action;Enabled=$Enabled;Protocol=$Protocol;RemoteAddress=$RemoteAddress;RemotePort=$RemotePort}
            if($Program.Contains('/') -or $Program -cne [IO.Path]::GetFullPath($Program)){throw 'Noncanonical Windows firewall application path'}
            foreach($address in $RemoteAddress){if($address -match '^(.+)/(\d+)$' -and $Matches[1].Contains(':') -and [int]$Matches[2] -lt 1){throw 'Windows rejects zero-bit IPv6 firewall prefixes'}}
            foreach($address in $RemoteAddress){if($address -match '^(.+)/(\d+)$' -and $Matches[1].Contains(':') -and $Matches[1] -in @('::','::1')){throw 'Windows rejects unspecified/loopback IPv6 subnet addresses'}}
        }
        function Get-NetFirewallRule { [CmdletBinding()]param($PolicyStore,$Name) return $script:firewallSpecifications[$Name] }
        function Get-NetFirewallApplicationFilter { [CmdletBinding()]param([Parameter(ValueFromPipeline)]$InputObject) process { [pscustomobject]@{Program=$InputObject.Program} } }
        & $original['Enable-PCIsolation'] $context
        Assert-Fixture ($context.Firewall.Count -eq 16)
        Assert-Fixture (@($script:firewallSpecifications.Values|Where-Object Direction -eq 'Inbound').Count -eq 4)
        foreach ($specification in $script:firewallSpecifications.Values) {
            Assert-Fixture (Test-PCWithin $specification.Program $context.Candidate)
            Assert-Fixture ($specification.Direction -in @('Outbound','Inbound') -and $specification.Action -ceq 'Block')
            if ($specification.Protocol -eq 'Any') { Assert-Fixture ('::2/1' -in $specification.RemoteAddress -and '8000::/1' -in $specification.RemoteAddress -and '127.0.0.2-255.255.255.255' -in $specification.RemoteAddress) }
            else { Assert-Fixture ($specification.RemoteAddress -ceq '127.0.0.1' -and '1-28864' -in $specification.RemotePort -and '28867-65535' -in $specification.RemotePort) }
        }
        $context.Owned=[Collections.Generic.List[object]]::new()
        function Remove-NetFirewallRule { [CmdletBinding()]param($Name) $script:firewallSpecifications.Remove($Name) }
        & $original['Disable-PCIsolation'] $context
        Assert-Fixture ($context.Firewall.Count -eq 0 -and $script:firewallSpecifications.Count -eq 0)
        function Get-NetFirewallProfile { @([pscustomobject]@{Enabled=$false},[pscustomobject]@{Enabled=$true},[pscustomobject]@{Enabled=$true}) }
        Expect-Failure { & $original['Enable-PCIsolation'] $context } '*already be enabled*'
    }
    Test-Fixture 'absolute configuration path rejected before candidate launch' {
        $f=New-Fixture; [IO.File]::WriteAllText("$($f.Root)/Server/Config/ExternalPath.txt",'C:/production/world')
        Expect-Failure { Invoke-Fixture $f } '*gate failed*'
        Assert-Fixture ((Read-FixtureReceipt $f).failureStage -ceq 'overlay' -and $script:launches.Count -eq 0)
    }
    Test-Fixture 'fresh bridge requires candidate PID, progressing current heartbeat' {
        $f=New-Fixture; $directory="$($f.Base)/Candidate/Server/APFDiscordBridge"; $null=[IO.Directory]::CreateDirectory($directory)
        $now=[DateTime]::UtcNow
        $context=@{Candidate="$($f.Base)/Candidate";Game=@{Process=@{Id=70001};StartedUtc=$now}}
        Write-PCJson "$directory/ready.json" @{pid=70001;startedAt=$now.ToString('o')}
        Write-PCJson "$directory/status.json" @{serverPid=101;updatedAt=$now.ToString('o');playerCount=0;connectedCount=0}
        Expect-Failure { Read-PCBridge $context } '*bridge*'
        Write-PCJson "$directory/status.json" @{serverPid=70001;updatedAt=$now.AddSeconds(-30).ToString('o');playerCount=0;connectedCount=0}
        Expect-Failure { Read-PCBridge $context } '*bridge*'
        Write-PCJson "$directory/status.json" @{serverPid=70001;updatedAt=$now.ToString('o');playerCount=0;connectedCount=0}
        Assert-Fixture (-not [string]::IsNullOrEmpty((Read-PCBridge $context)))
    }
    Test-Fixture 'fatal startup rejected despite all-systems marker' {
        $f=New-Fixture; $null=[IO.Directory]::CreateDirectory("$($f.Base)/Logs")
        [IO.File]::WriteAllText("$($f.Base)/Logs/startup.log","All systems up and running.`nFATAL startup exploded")
        Expect-Failure { Read-PCStartup @{Scratch=$f.Base;Candidate="$($f.Base)/Candidate"} } '*startup log*'
    }
    Test-Fixture 'native launch plan excludes inherited task/hook routes and uses fixed loopback arguments' {
        $f=New-Fixture; $context=@{Candidate=$f.Root;Scratch=$f.Base;Version='kesas-rc999';PackPort=28866}
        $previousTask=$env:APF_SERVER_TASK; $previousHook=$env:DOTNET_STARTUP_HOOKS
        try {
            $env:APF_SERVER_TASK='fixture-production-task'; $env:DOTNET_STARTUP_HOOKS='fixture-inherited-hook'
            $start=New-PCStartInfo $context Pack
            Assert-Fixture (-not $start.Environment.ContainsKey('APF_SERVER_TASK') -and -not $start.Environment.ContainsKey('DOTNET_STARTUP_HOOKS'))
            Assert-Fixture ($start.CreateNoWindow -and $start.WindowStyle -eq 'Hidden' -and -not $start.UseShellExecute)
            Assert-Fixture ($start.RedirectStandardInput -and $start.RedirectStandardOutput -and $start.RedirectStandardError)
            Assert-Fixture ($start.ArgumentList.Contains('https://127.0.0.1:28866') -and $start.ArgumentList.Contains('--client-update-root'))
            $game=New-PCStartInfo $context Game
            Assert-Fixture ($game.FileName -ceq [IO.Path]::GetFullPath("$($f.Root)/Server/Server.exe") -and $game.ArgumentList.Count -eq 0)
        } finally { $env:APF_SERVER_TASK=$previousTask; $env:DOTNET_STARTUP_HOOKS=$previousHook }
    }
    Test-Fixture 'native redirection helper writes private stdout/stderr and exits bounded' {
        $f=New-Fixture; $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'; $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in @('-NoProfile','-NonInteractive','-Command',"[Console]::WriteLine('fixture-out'); [Console]::Error.WriteLine('fixture-error')")) { $start.ArgumentList.Add($argument) }
        $owned=[PreparedCandidate.ProcessLog]::new($start,"$($f.Base)/out.log","$($f.Base)/error.log")
        try { $owned.AssertOwned(); Assert-Fixture ($owned.Process.WaitForExit(20000)); $owned.Drain(); Assert-Fixture ((Read-PCText "$($f.Base)/out.log").Contains('fixture-out')); Assert-Fixture ((Read-PCText "$($f.Base)/error.log").Contains('fixture-error')) }
        finally { if (-not $owned.Process.HasExited) { $owned.AssertOwned(); $owned.Process.Kill(); $null=$owned.Process.WaitForExit(5000) }; $owned.Dispose() }
    }
    Test-Fixture 'HTTPS helper forbids nonloopback without connecting' {
        $f=New-Fixture
        Expect-Failure { [PreparedCandidate.Https]::Fetch('https://192.0.2.1:18866/health',('a'*64),"$($f.Base)/forbidden",100) } '*Only candidate loopback HTTPS*'
        Assert-Fixture (-not (Test-Path -LiteralPath "$($f.Base)/forbidden"))
    }
    Test-Fixture 'real loopback TLS pins certificate, hashes GET bytes, refuses wrong pin/redirect' {
        $f=New-Fixture; $pin=(Read-PCText "$($f.Root)/deployment.json" | ConvertFrom-Json).certificateSha256
        $server=[PreparedCandidateTlsFixture]::new("$($f.Root)/secrets/pack.pfx",'fixture-signed-body',200)
        try {
            $hash=[PreparedCandidate.Https]::Fetch("https://127.0.0.1:$($server.Port)/fixture",$pin,"$($f.Base)/download",100)
            Assert-Fixture ($hash -ceq (Get-PCHash "$($f.Base)/download") -and (Read-PCText "$($f.Base)/download") -ceq 'fixture-signed-body')
        } catch { throw "$($_.Exception.GetBaseException().Message); fixture TLS server: $($server.ErrorMessage)" }
        finally { $server.Dispose() }
        $server=[PreparedCandidateTlsFixture]::new("$($f.Root)/secrets/pack.pfx",'fixture-signed-body',200)
        try { Expect-Failure { [PreparedCandidate.Https]::Fetch("https://127.0.0.1:$($server.Port)/fixture",('a'*64),"$($f.Base)/wrong-pin",100) } }
        finally { $server.Dispose() }
        Assert-Fixture (-not (Test-Path -LiteralPath "$($f.Base)/wrong-pin"))
        $server=[PreparedCandidateTlsFixture]::new("$($f.Root)/secrets/pack.pfx",'fixture-signed-body',302)
        try { Expect-Failure { [PreparedCandidate.Https]::Fetch("https://127.0.0.1:$($server.Port)/fixture",$pin,"$($f.Base)/redirect",100) } '*GET rejected*' }
        finally { $server.Dispose() }
    }
} finally {
    foreach ($name in $original.Keys) { Set-FixtureMock $name $original[$name] }
    # This exact GUID fixture directory is the only deletion target.
    $resolved=[IO.Path]::GetFullPath($scratchBase)
    $temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^APF-prepared-fixtures-[a-f0-9]{32}$') { throw 'Refusing fixture cleanup outside temporary directory' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output "Fixtures: $script:passed passed; $script:failed failed. Native boot/firewall/signature-tool/Admin-SYSTEM ACL execution NOT exercised."
if ($script:failed) { exit 1 }
