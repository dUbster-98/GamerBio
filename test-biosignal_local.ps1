[CmdletBinding()]
param(
    [string]$Url = 'https://localhost:7211/api/biosignal',
    [string]$ApiKey = "989a860f8d8b9b8aaa819007a470e911a52caae243365a8d0074ac25a5c69c21",
    [int]$IntervalSeconds = 1,
    [int]$Count = 0,
    [switch]$Once
)

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'API key not provided. Pass -ApiKey "..." or set $env:BIOMONITOR_API_KEY first.'
}

$bpm  = 75
$gsr  = 400
$temp = 35.5
$sent = 0

Write-Host "POST $Url  (Ctrl+C to stop)" -ForegroundColor Cyan

while ($true) {
    $bpm  = [int][Math]::Max(55,  [Math]::Min(180, $bpm  + (Get-Random -Minimum -3 -Maximum 4)))
    $gsr  = [int][Math]::Max(250, [Math]::Min(900, $gsr  + (Get-Random -Minimum -20 -Maximum 25)))
    $temp = [Math]::Round([Math]::Max(33.0, [Math]::Min(37.5, $temp + (Get-Random -Minimum -2 -Maximum 3) * 0.1)), 2)

    $spike = ((Get-Random -Maximum 10) -eq 0)
    if ($spike) {
        $bpm = [int][Math]::Min(180, $bpm + (Get-Random -Minimum 20 -Maximum 40))
        $gsr = [int][Math]::Min(900, $gsr + (Get-Random -Minimum 100 -Maximum 200))
    }

    $body = @{
        bpm       = $bpm
        gsr       = $gsr
        skinTemp  = $temp
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json -Compress

    try {
        $resp = Invoke-RestMethod -Method Post -Uri $Url `
            -Headers @{ 'X-Api-Key' = $ApiKey } `
            -ContentType 'application/json' `
            -Body $body
        $tag = if ($spike) { '⚡' } else { ' ' }
        Write-Host ("[{0:HH:mm:ss}] {1} BPM={2,3} GSR={3,3} Temp={4} -> id={5}" -f `
            (Get-Date), $tag, $bpm, $gsr, $temp, $resp.id) -ForegroundColor Green
    } catch {
        Write-Host ("[{0:HH:mm:ss}] ERROR: {1}" -f (Get-Date), $_.Exception.Message) -ForegroundColor Red
    }

    $sent++
    if ($Once) { break }
    if ($Count -gt 0 -and $sent -ge $Count) { break }
    Start-Sleep -Seconds $IntervalSeconds
}
