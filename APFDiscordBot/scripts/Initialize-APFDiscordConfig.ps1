param(
  [Parameter(Mandatory = $true)] [string] $GuildId,
  [Parameter(Mandatory = $true)] [string] $DashboardChannelId,
  [Parameter(Mandatory = $true)] [string] $ConsoleChannelId,
  [Parameter(Mandatory = $true)] [string] $LunaChatChannelId,
  [string[]] $AdminRoleId = @(),
  [string[]] $OperatorRoleId = @(),
  [string[]] $ModeratorRoleId = @()
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $root 'config.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "Missing config.json. Copy config.example.json first." }
function Assert-Snowflake([string] $value, [string] $label) {
  if ($value -notmatch '^\d{17,20}$') { throw "$label must be a Discord snowflake (17-20 digits)." }
}
Assert-Snowflake $GuildId 'GuildId'
Assert-Snowflake $DashboardChannelId 'DashboardChannelId'
Assert-Snowflake $ConsoleChannelId 'ConsoleChannelId'
Assert-Snowflake $LunaChatChannelId 'LunaChatChannelId'
foreach ($role in @($AdminRoleId + $OperatorRoleId + $ModeratorRoleId)) { Assert-Snowflake $role 'RoleId' }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.discord.guildId = $GuildId
$config.discord.channels.dashboard = $DashboardChannelId
$config.discord.channels.console = $ConsoleChannelId
$config.discord.channels.lunaChat = $LunaChatChannelId
$config.discord.adminRoleIds = @($AdminRoleId)
$config.discord.operatorRoleIds = @($OperatorRoleId)
$config.discord.moderatorRoleIds = @($ModeratorRoleId)
$temp = "$configPath.$PID.tmp"
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temp -Encoding UTF8
Move-Item -LiteralPath $temp -Destination $configPath -Force
Write-Output "Discord IDs written to $configPath. Token remains environment-only."
