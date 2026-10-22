<#
.SYNOPSIS
    Copies a published build of Zapim Studio into its IIS site folder and checks that it starts.

.DESCRIPTION
    Takes the site offline with app_offline.htm (the ASP.NET Core Module stops the app and releases its files),
    mirrors the build into the site folder while keeping the server-only appsettings.Production.json,
    then brings the site back and waits for /health. On a failed copy the offline page stays up so a
    half-copied build is never served.

.EXAMPLE
    .\deploy.ps1 -Source .\publish -SitePath C:\inetpub\zapim-studio -HealthUrl https://studio.example.com/health
#>
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$SitePath,
    [string]$HealthUrl = ''
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

if (-not (Test-Path (Join-Path $Source 'Zapim.Marketing.dll'))) { throw "No published build found in $Source." }
if (-not (Test-Path (Join-Path $SitePath 'appsettings.Production.json'))) {
    throw "$SitePath\appsettings.Production.json is missing. Run deploy\iis\setup-server.ps1 on this server first."
}

$offline = Join-Path $SitePath 'app_offline.htm'
Set-Content -Path $offline -Encoding UTF8 -Value '<!doctype html><meta charset="utf-8"><title>Updating</title><p style="font-family:sans-serif">Zapim Studio is being updated. Please try again in a minute.</p>'
Start-Sleep -Seconds 5

robocopy $Source $SitePath /MIR /XF appsettings.Production.json app_offline.htm /XD logs /R:5 /W:3 /NFL /NDL /NP /NJH
# robocopy: 0-7 mean success (files copied, extras removed); 8 and above are failures.
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE. The site stays offline; fix the problem and re-run the deployment." }
$global:LASTEXITCODE = 0

Remove-Item $offline
Write-Host "Deployed $Source to $SitePath."

if ($HealthUrl) {
    $deadline = (Get-Date).AddSeconds(90)
    $lastError = ''
    while ($true) {
        try {
            $response = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 15
            if ($response.StatusCode -eq 200) { Write-Host "Healthy: $HealthUrl"; break }
            $lastError = "HTTP $($response.StatusCode)"
        } catch { $lastError = $_.Exception.Message }
        if ((Get-Date) -gt $deadline) { throw "The site did not become healthy at $HealthUrl within 90 seconds ($lastError). Check Event Viewer > Application (IIS AspNetCore Module V2)." }
        Start-Sleep -Seconds 3
    }
} else {
    Write-Warning 'ZAPIM_HEALTH_URL is not set; skipped the health check.'
}
exit 0
