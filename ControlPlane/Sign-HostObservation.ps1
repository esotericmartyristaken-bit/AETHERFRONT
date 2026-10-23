#requires -Version 7.4
param([Parameter(Mandatory)][string]$Settings)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
$root=[IO.Path]::GetFullPath($configuration.root)
if($root -ine 'C:\ProgramData\Aetherfront-Control'){throw 'Unexpected observation root'}
$inputPath=Join-Path $root 'observation-payload.json'
$certificatePath=Join-Path $configuration.nativeRoot 'secrets/pack.pfx'
foreach($file in @($Settings,$inputPath,$certificatePath)){
    for($cursor=[IO.Path]::GetFullPath($file);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe observation source'}
    }
}
$stream=[IO.FileStream]::new($inputPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{
    if($stream.Length -le 0 -or $stream.Length -gt 65536){throw 'Observation payload size rejected'}
    $bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes,0,$bytes.Length)
}finally{$stream.Dispose()}
$text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
$document=[Text.Json.JsonDocument]::Parse($text)
try{
    $value=$document.RootElement
    $names=@($value.EnumerateObject()|ForEach-Object Name)
    if($names.Count -ne 3 -or @($names|Sort-Object -Unique).Count -ne 3 -or @($names|Where-Object {$_ -cnotin @('schemaVersion','status','diagnostics')}).Count -or $value.GetProperty('schemaVersion').GetInt32() -ne 1){throw 'Observation payload rejected'}
    $status=$value.GetProperty('status');$diagnostics=$value.GetProperty('diagnostics')
    $observedText=$status.GetProperty('observedAt').GetString();$expiresText=$status.GetProperty('expiresAt').GetString()
    $observed=[DateTimeOffset]::Parse($observedText);$expires=[DateTimeOffset]::Parse($expiresText)
    if($observedText -cne $diagnostics.GetProperty('observedAt').GetString() -or $expiresText -cne $diagnostics.GetProperty('expiresAt').GetString() -or $observed -gt [DateTimeOffset]::UtcNow.AddSeconds(5) -or $expires -le [DateTimeOffset]::UtcNow -or $expires -le $observed -or ($expires-$observed).TotalSeconds -gt 30){throw 'Observation freshness rejected'}
}finally{$document.Dispose()}
$certificatePassword=[Environment]::GetEnvironmentVariable('APF_PACK_HOST_CERT_PASSWORD','Process')
if($null -eq $certificatePassword){$certificatePassword=''}
$certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath,$certificatePassword,[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
$certificatePassword=$null
$key=$null
try{
    $key=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    if(-not $key -or $key.KeySize -lt 2048){throw 'Observation signing identity unavailable'}
    $context=[Text.Encoding]::UTF8.GetBytes("AETHERFRONT CONTROL READBACK v1`n")
    $signed=[byte[]]::new($context.Length+$bytes.Length)
    [Array]::Copy($context,0,$signed,0,$context.Length);[Array]::Copy($bytes,0,$signed,$context.Length,$bytes.Length)
    $signature=$key.SignData($signed,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [ordered]@{schemaVersion=1;payload=[Convert]::ToBase64String($bytes);certificate=[Convert]::ToBase64String($certificate.RawData);signature=[Convert]::ToBase64String($signature)}|ConvertTo-Json -Compress
}finally{if($key){$key.Dispose()};$certificate.Dispose()}
