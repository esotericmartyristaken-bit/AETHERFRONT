#requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$directory = Join-Path $localAppData 'Aetherfront-GitHubReleaseSigner'
$privatePath = Join-Path $directory 'controller-key.dpapi'
$publicPath = Join-Path $directory 'controller-key.pub.pem'
$owner = $identity.User
$trusted = @($owner.Value, 'S-1-5-18', 'S-1-5-32-544')
for ($ancestor = [IO.DirectoryInfo]::new($localAppData); $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Controller key path traverses a reparse point'
    }
}
if (![IO.Directory]::Exists($directory)) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($owner)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in $trusted) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl',
            'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($directory), $acl)
}
$keyDirectory = Get-Item -LiteralPath $directory -Force
$acl = Get-Acl -LiteralPath $directory
if (($keyDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    !$acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $owner.Value -or
    @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
      Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin $trusted }).Count) {
    throw 'Controller key directory ACL is not private'
}
if ([IO.File]::Exists($privatePath) -xor [IO.File]::Exists($publicPath)) {
    throw 'Controller key pair is incomplete and needs review'
}
if (![IO.File]::Exists($privatePath)) {
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $keyData = (& node -e @'
const {generateKeyPairSync}=require('node:crypto');
const {publicKey,privateKey}=generateKeyPairSync('ed25519');
process.stdout.write(JSON.stringify({
  secret:privateKey.export({format:'der',type:'pkcs8'}).toString('base64'),
  public:publicKey.export({format:'pem',type:'spki'})
}));
'@)
    if ($LASTEXITCODE -ne 0) { throw 'Controller key generation failed' }
    $key = $keyData | ConvertFrom-Json
    $secret = [Convert]::FromBase64String($key.secret)
    try {
        $entropy = [Text.Encoding]::ASCII.GetBytes('AETHERFRONT GITHUB RELEASE SIGNER v3')
        $encrypted = [Security.Cryptography.ProtectedData]::Protect(
            $secret, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        foreach ($item in @(@{Path=$privatePath;Bytes=$encrypted},
                             @{Path=$publicPath;Bytes=[Text.Encoding]::ASCII.GetBytes($key.public)})) {
            $stream = [IO.FileStream]::new($item.Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $stream.Write($item.Bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        }
    } finally { [Array]::Clear($secret); $key = $null; $keyData = $null }
}
foreach ($path in @($privatePath, $publicPath)) {
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Controller key file is a reparse point'
    }
}
[pscustomobject]@{
    PublicKeyPath = $publicPath
    PublicKeySha256 = (Get-FileHash -LiteralPath $publicPath).Hash.ToLowerInvariant()
}
