param([Parameter(Mandatory)][string]$Settings)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Security
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
$credential=Join-Path $configuration.root 'bootstrap/credentials/github.dpapi'
$bytes=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($credential),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
try{$env:GH_TOKEN=[Text.Encoding]::UTF8.GetString($bytes)}finally{[Array]::Clear($bytes,0,$bytes.Length)}
$lease=[IO.FileStream]::new((Join-Path $configuration.root 'managed/.anchor.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{& $configuration.node "$PSScriptRoot/anchor.mjs" --settings $Settings;exit $LASTEXITCODE}
finally{$lease.Dispose();$env:GH_TOKEN=$null}
