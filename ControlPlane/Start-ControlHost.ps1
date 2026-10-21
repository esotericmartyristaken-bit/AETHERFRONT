param([Parameter(Mandatory)][string]$Settings)
$ErrorActionPreference='Stop'
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
Set-Location -LiteralPath $configuration.botRoot
if(-not $env:APF_DISCORD_TOKEN){$env:APF_DISCORD_TOKEN=[Environment]::GetEnvironmentVariable('APF_DISCORD_TOKEN','User')}
if(-not $env:APF_DISCORD_TOKEN){throw 'Discord credential is not provisioned in the existing user environment.'}
$leasePath=Join-Path $configuration.root '.supervisor.lock'
for($cursor=$leasePath;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe supervisor lease path'}
}
$lease=[IO.FileStream]::new($leasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $env:APF_SUPERVISOR_PARENT_PID=[string]$PID
    & $configuration.node (Join-Path $PSScriptRoot 'bootstrap.mjs') --settings $Settings
    exit $LASTEXITCODE
} finally {$lease.Dispose()}
