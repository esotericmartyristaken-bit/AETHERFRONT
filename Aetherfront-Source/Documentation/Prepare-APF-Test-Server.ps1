param(
    [Parameter(Mandatory = $true)]
    [string]$ServerRoot,

    [int]$Port = 2263,
    [string]$ServerName = "Aetherfront Client Test",
    [string]$Password = "aetherfront-dev",
    [string]$MasterServer = "127.0.0.1:18700",
    [string]$ManifestUrl = "https://85-239-237-125.sslip.io:18701/packs/APFPack.json"
)

$ErrorActionPreference = "Stop"
$configRoot = Join-Path $ServerRoot "Config"

function Set-ApfXmlValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $node = $document.SelectSingleNode("//*[local-name()='$Name']")
    if ($null -eq $node) {
        throw "Missing XML setting '$Name' in '$Path'."
    }

    $node.InnerText = $Value
    $settings = [System.Xml.XmlWriterSettings]::new()
    # LMP's generated config files are UTF-16 LE. Keep the BOM so Windows
    # PowerShell and the server deserialize every subsequent edit correctly.
    $settings.Encoding = [System.Text.UnicodeEncoding]::new($false, $true)
    $settings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $configRoot -PathType Container)) {
    throw "Server Config directory not found: $configRoot"
}

Set-ApfXmlValue (Join-Path $configRoot "ConnectionSettings.xml") "Port" $Port.ToString()
Set-ApfXmlValue (Join-Path $configRoot "GeneralSettings.xml") "ServerName" $ServerName
Set-ApfXmlValue (Join-Path $configRoot "GeneralSettings.xml") "Description" "Isolated APF client validation server"
Set-ApfXmlValue (Join-Path $configRoot "GeneralSettings.xml") "Password" $Password
Set-ApfXmlValue (Join-Path $configRoot "GeneralSettings.xml") "MaxPlayers" "8"
Set-ApfXmlValue (Join-Path $configRoot "GeneralSettings.xml") "AutoDekessler" "0"
Set-ApfXmlValue (Join-Path $configRoot "GeneralSettings.xml") "NumberOfAsteroids" "0"
Set-ApfXmlValue (Join-Path $configRoot "GeneralSettings.xml") "NumberOfComets" "0"
Set-ApfXmlValue (Join-Path $configRoot "DebugSettings.xml") "CustomMasterServer" $MasterServer
Set-ApfXmlValue (Join-Path $configRoot "MasterServerSettings.xml") "RegisterWithMasterServer" "true"
Set-ApfXmlValue (Join-Path $configRoot "WebsiteSettings.xml") "EnableWebsite" "false"

[System.IO.File]::WriteAllText(
    (Join-Path $configRoot "APFManifestUrl.txt"),
    $ManifestUrl + [Environment]::NewLine,
    [System.Text.Encoding]::ASCII)

Write-Output "Prepared isolated APF test server '$ServerName' on UDP $Port."
