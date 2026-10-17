#Requires -RunAsAdministrator

param(
    [int]$MasterUdpPort = 18700,
    [int]$ServerUdpPort = 2252,
    [int]$MasterHttpPort = 18701
)

$rules = @(
    @{ Name = 'APF Master UDP 18700'; Protocol = 'UDP'; Port = $MasterUdpPort },
    @{ Name = 'APF LMP Server UDP 2252'; Protocol = 'UDP'; Port = $ServerUdpPort },
    @{ Name = 'APF Master Web TCP 18701'; Protocol = 'TCP'; Port = $MasterHttpPort }
)

foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
    if ($existing) {
        Set-NetFirewallRule -DisplayName $rule.Name -Enabled True -Action Allow -Direction Inbound -Profile Any
        continue
    }

    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Protocol $rule.Protocol -LocalPort $rule.Port -Profile Any | Out-Null
}

Write-Host 'APF inbound firewall rules configured.'
