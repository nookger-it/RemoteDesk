<#
.SYNOPSIS
Configures Windows Defender Firewall rules for RemoteDesk V1 LAN-First.
Strictly scopes inbound rules to LocalSubnet / Private profile only (Never Public Internet).
Requires Administrator privileges.
#>
param(
    [switch]$ApplyRules,
    [switch]$RemoveRules
)

$ErrorActionPreference = "Stop"

if ($RemoveRules) {
    Get-NetFirewallRule -DisplayName "RemoteDesk V1*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "Removed all RemoteDesk V1 firewall rules." -ForegroundColor Yellow
    return
}

Write-Host "=== RemoteDesk V1 LAN Firewall Plan ===" -ForegroundColor Cyan
Write-Host "1. Local Management API (TCP 5100 / 443)  -> Profile: Private/Domain, RemoteAddress: LocalSubnet"
Write-Host "2. Local Relay Service  (TCP 52100)       -> Profile: Private/Domain, RemoteAddress: LocalSubnet"
Write-Host "3. Direct Agent Stream  (TCP 52000-52010) -> Profile: Private/Domain, RemoteAddress: LocalSubnet"

if (-not $ApplyRules) {
    Write-Host "`nDry-run completed. Re-run with -ApplyRules as Administrator to apply rules." -ForegroundColor Gray
    return
}

New-NetFirewallRule -DisplayName "RemoteDesk V1 - Local Management API" `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5100,443 `
    -Profile Private,Domain -RemoteAddress LocalSubnet

New-NetFirewallRule -DisplayName "RemoteDesk V1 - Local Server Relay" `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort 52100 `
    -Profile Private,Domain -RemoteAddress LocalSubnet

New-NetFirewallRule -DisplayName "RemoteDesk V1 - Direct LAN Agent Stream" `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort 52000-52010 `
    -Profile Private,Domain -RemoteAddress LocalSubnet

Write-Host "Applied RemoteDesk V1 LAN-scoped firewall rules successfully." -ForegroundColor Green

