param([Parameter(Mandatory)][string]$Settings)
$ErrorActionPreference='Stop'
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
Set-Location -LiteralPath $configuration.botRoot
$env:APF_SERVER_TASK='KESAS Server'
& $configuration.node (Join-Path $PSScriptRoot 'native-agent.mjs') --settings $Settings
exit $LASTEXITCODE
