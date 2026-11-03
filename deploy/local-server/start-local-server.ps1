<#
.SYNOPSIS
Starts the RemoteDesk V1 Local Server stack (Management API on port 5100 + Local Relay Service on port 52100) on PC-A.
#>
param(
    [string]$ApiUrls = "http://0.0.0.0:5100",
    [int]$RelayPort = 52100
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

Write-Host "=== RemoteDesk V1 Local Server Stack (PC-A) ===" -ForegroundColor Cyan
Write-Host "Building Backend API and Local Relay in Release mode..."
dotnet build "$RepoRoot\src\backend\RemoteDesk.Api\RemoteDesk.Api.csproj" -c Release
dotnet build "$RepoRoot\src\backend\RemoteDesk.Relay\RemoteDesk.Relay.csproj" -c Release

Write-Host "Starting RemoteDesk.Relay on TCP/TLS port $RelayPort..." -ForegroundColor Green
$relayJob = Start-Process -FilePath "dotnet" `
    -ArgumentList "run --no-build -c Release --project `"$RepoRoot\src\backend\RemoteDesk.Relay\RemoteDesk.Relay.csproj`" -- --RemoteDesk:RelayPort=$RelayPort" `
    -PassThru

Write-Host "Starting RemoteDesk.Api on $ApiUrls (Relay PID=$($relayJob.Id))..." -ForegroundColor Green
try {
    dotnet run --no-build -c Release --project "$RepoRoot\src\backend\RemoteDesk.Api\RemoteDesk.Api.csproj" --urls $ApiUrls
}
finally {
    if (!$relayJob.HasExited) {
        Stop-Process -Id $relayJob.Id -Force
    }
}

