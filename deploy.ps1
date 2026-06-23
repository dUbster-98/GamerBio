[CmdletBinding()]
param(
    [string]$RemoteHost = 'tjdgus@192.168.0.104',
    [string]$RemotePath = '/opt/gamerbio',
    [string]$ServiceName = 'gamerbio.service',
    [string]$Project = (Join-Path $PSScriptRoot 'GamerBio\GamerBio.csproj'),
    [string]$PublishDir = (Join-Path $PSScriptRoot 'publish-arm64'),
    [switch]$SkipBuild,
    [switch]$FollowLogs
)

$ErrorActionPreference = 'Stop'

function Invoke-Step {
    param([string]$Label, [scriptblock]$Action)
    Write-Host "==> $Label" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Step failed ($Label) with exit code $LASTEXITCODE"
    }
}

if (-not $SkipBuild) {
    Invoke-Step 'dotnet publish (linux-arm64)' {
        dotnet publish $Project -c Release -r linux-arm64 --self-contained false -o $PublishDir
    }
} else {
    Write-Host '==> Skipping build (-SkipBuild)' -ForegroundColor Yellow
}

Invoke-Step "scp → ${RemoteHost}:${RemotePath}" {
    scp -r (Join-Path $PublishDir '*') "${RemoteHost}:${RemotePath}/"
}

Invoke-Step "restart $ServiceName" {
    ssh $RemoteHost "sudo systemctl restart $ServiceName"
}

Invoke-Step "status $ServiceName" {
    ssh $RemoteHost "systemctl status $ServiceName --no-pager"
}

if ($FollowLogs) {
    Write-Host '==> Following journalctl (Ctrl+C to stop)' -ForegroundColor Cyan
    ssh $RemoteHost "journalctl -u $ServiceName -f"
}
