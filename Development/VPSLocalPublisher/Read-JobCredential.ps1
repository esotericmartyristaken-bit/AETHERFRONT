#requires -Version 7.5
param([Parameter(Mandatory)][string]$Payload)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Publisher.Common.ps1')
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18'){throw 'Credential decryption requires the fixed SYSTEM worker'}
$config=Read-PublisherJson (Join-Path $PSScriptRoot 'runner.json')
Assert-PublisherPath $Payload $config.root
if((Get-Item -LiteralPath $Payload).Length -gt 4096){throw 'Credential envelope exceeds bound'}
$secret=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($config.credentialPrivateKeyPath),[Text.Encoding]::ASCII.GetBytes('AETHERFRONT JOB CREDENTIAL RSA v3'),[Security.Cryptography.DataProtectionScope]::LocalMachine)
$rsa=[Security.Cryptography.RSA]::Create()
try {
    $read=0;$rsa.ImportPkcs8PrivateKey($secret,[ref]$read)
    $plain=$rsa.Decrypt([IO.File]::ReadAllBytes($Payload),[Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
    try {
        $value=[Text.UTF8Encoding]::new($false,$true).GetString($plain)|ConvertFrom-Json
        if(@($value.PSObject.Properties).Count -lt 1 -or @($value.PSObject.Properties|Where-Object Name -NotIn @('githubToken','discordToken')).Count){throw 'Unsupported credential envelope'}
        foreach($property in $value.PSObject.Properties){if($property.Value -isnot [string] -or $property.Value.Length -lt 8 -or $property.Value.Length -gt 1024 -or $property.Value -match '[\x00-\x20]'){throw 'Invalid publication credential'}}
        # This output is consumed only through the SYSTEM child's private pipe.
        $value|ConvertTo-Json -Compress
    } finally {[Array]::Clear($plain)}
} finally {$rsa.Dispose();[Array]::Clear($secret)}
