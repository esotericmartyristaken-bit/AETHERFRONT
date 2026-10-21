param([Parameter(Mandatory)][string]$Settings)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Security
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
Set-Location -LiteralPath $configuration.botRoot
$credential=Join-Path $configuration.root 'bootstrap/bot-credential.dpapi'
$bytes=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($credential),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
try{$env:APF_DISCORD_TOKEN=[Text.Encoding]::UTF8.GetString($bytes)}finally{[Array]::Clear($bytes,0,$bytes.Length)}
$pointer=Get-Content -LiteralPath (Join-Path $configuration.root 'managed/active.json') -Raw|ConvertFrom-Json
if($pointer.directory -cnotmatch '^control-[1-9][0-9]{0,8}-[a-f0-9]{64}$'){throw 'Invalid protected control selection'}
$release=Join-Path $configuration.root "managed/releases/$($pointer.directory)"
$env:APF_MANAGED_CONTROL='1'
try {
    & $configuration.node (Join-Path $release 'ControlPlane/worker.mjs') --settings $Settings
    exit $LASTEXITCODE
} finally {$env:APF_DISCORD_TOKEN=$null}
