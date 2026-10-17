param(
    [Parameter(Mandatory = $true)] [string]$RootDirectory,
    [string]$ListenUrl = 'https://127.0.0.1:18443',
    [Parameter(Mandatory = $true)] [string]$CertificatePath,
    [string]$CertificatePassword = ''
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $sourceRoot 'ApfPackHost\ApfPackHost.csproj'
$dotnet = Join-Path (Split-Path -Parent $sourceRoot) '.dotnet-sdk6\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = 'dotnet' }
if (-not (Test-Path -LiteralPath $RootDirectory -PathType Container)) { throw "Pack root does not exist: $RootDirectory" }
if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) { throw "Certificate does not exist: $CertificatePath" }

$env:APF_PACK_HOST_CERT_PASSWORD = $CertificatePassword
& $dotnet run --project $project --configuration Release -- --root $RootDirectory --listen-url $ListenUrl --certificate-path $CertificatePath
exit $LASTEXITCODE
