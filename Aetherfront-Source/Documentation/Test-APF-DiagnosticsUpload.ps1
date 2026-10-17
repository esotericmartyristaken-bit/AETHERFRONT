param(
    [string]$Endpoint = "http://127.0.0.1:18444/v1/diagnostics",
    [Parameter(Mandatory = $true)][string]$TokenPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$runRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ([Guid]::NewGuid().ToString("N"))
$sourceRoot = Join-Path $runRoot "source"
[IO.Directory]::CreateDirectory($sourceRoot) | Out-Null
$reportId = "AF-INTEGRATION-" + [Guid]::NewGuid().ToString("N").Substring(0, 12)
$manifest = @{
    context = @{
        schemaVersion = 1
        reportId = $reportId
        createdUtc = [DateTime]::UtcNow.ToString("O")
        clientVersion = "apf-client-2"
        kspVersion = "1.12.5"
        scene = "MainMenu"
        networkState = "diagnostics-smoke"
        serverAddress = ""
        serverPort = 0
        playerName = ""
        sessionId = "integration-smoke"
        description = "diagnostics host integration"
        operatingSystem = "Windows"
        is64BitProcess = $true
        privateMemoryBytes = 0
    }
    entries = @()
    privacy = "No saves, craft files, screenshots, or arbitrary files are included."
} | ConvertTo-Json -Depth 8 -Compress
[IO.File]::WriteAllText((Join-Path $sourceRoot "diagnostic.json"), $manifest, [Text.UTF8Encoding]::new($false))
$archivePath = Join-Path $runRoot ($reportId + ".zip")
[IO.Compression.ZipFile]::CreateFromDirectory($sourceRoot, $archivePath)

$token = [IO.File]::ReadAllText([IO.Path]::GetFullPath($TokenPath)).Trim()
$reply = Invoke-RestMethod -Method Post -Uri $Endpoint -ContentType "application/zip" -Headers @{
    "X-Aetherfront-Upload-Token" = $token
    "X-Aetherfront-Report-Id" = $reportId
} -InFile $archivePath -TimeoutSec 30

if ([string]::IsNullOrWhiteSpace($reply.caseId) -or $reply.receivedBytes -le 0) {
    throw "Diagnostics host returned an invalid reply."
}

[pscustomobject]@{
    ReportId = $reportId
    CaseId = $reply.caseId
    ReceivedBytes = $reply.receivedBytes
    ArchivePath = $archivePath
}
