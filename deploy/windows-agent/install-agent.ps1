<#
.SYNOPSIS
Builds and starts the RemoteDesk V1 Windows Agent (Agent.Service + Agent.Interactive) on target PC-B.
Can also register Agent.Service as a Windows Service when -RegisterWindowsService is specified (requires Administrator).
#>
param(
    [string]$ApiBaseUrl = "http://192.168.1.10:5100",
    [int]$DirectLanPort = 52000,
    [switch]$RegisterWindowsService
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

Write-Host "=== RemoteDesk V1 Windows Agent Installer & Runner (PC-B) ===" -ForegroundColor Cyan
dotnet publish "$RepoRoot\src\agent\RemoteDesk.Agent.Service\RemoteDesk.Agent.Service.csproj" -c Release -o "$RepoRoot\artifacts\agent-service"
dotnet publish "$RepoRoot\src\agent\RemoteDesk.Agent.Interactive\RemoteDesk.Agent.Interactive.csproj" -c Release -o "$RepoRoot\artifacts\agent-interactive"

if ($RegisterWindowsService) {
    $svcExe = Join-Path "$RepoRoot\artifacts\agent-service" "RemoteDesk.Agent.Service.exe"
    Write-Host "Registering Windows Service 'RemoteDeskAgentService' -> $svcExe" -ForegroundColor Yellow
    sc.exe create RemoteDeskAgentService binPath= "`"$svcExe`" --RemoteDesk:ApiBaseUrl=$ApiBaseUrl --RemoteDesk:DirectLanPort=$DirectLanPort" start= auto
    sc.exe description RemoteDeskAgentService "RemoteDesk V1 LAN-First Device Identity & Heartbeat Service"
    sc.exe start RemoteDeskAgentService
    Write-Host "Windows Service registered and started." -ForegroundColor Green
    return
}

Write-Host "Starting Agent.Interactive (User Session Screen Capture & Input Worker)..." -ForegroundColor Green
$interactiveProc = Start-Process -FilePath "dotnet" `
    -ArgumentList "`"$RepoRoot\artifacts\agent-interactive\RemoteDesk.Agent.Interactive.dll`"" `
    -PassThru

Write-Host "Starting Agent.Service connected to $ApiBaseUrl (Direct LAN Port $DirectLanPort)..." -ForegroundColor Green
try {
    dotnet "$RepoRoot\artifacts\agent-service\RemoteDesk.Agent.Service.dll" `
        "--RemoteDesk:ApiBaseUrl=$ApiBaseUrl" `
        "--RemoteDesk:DirectLanPort=$DirectLanPort"
}
finally {
    if (!$interactiveProc.HasExited) {
        Stop-Process -Id $interactiveProc.Id -Force
    }
}

