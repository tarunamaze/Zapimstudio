#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time IIS setup for Zapim Studio on Windows Server. Safe to re-run.

.DESCRIPTION
    Creates the app pool, site, data folder and server-only configuration, and (if sqlcmd is installed)
    the SQL Server database. Deployments themselves are done by deploy.ps1 from GitHub Actions.

.EXAMPLE
    .\setup-server.ps1 -HostName studio.example.com -BootstrapEmail admin@example.com -CertificateThumbprint 0123ABCD...
#>
param(
    [Parameter(Mandatory)][string]$HostName,
    [Parameter(Mandatory)][string]$BootstrapEmail,
    [securestring]$BootstrapPassword,
    [string]$CertificateThumbprint = '',
    [string]$SiteName = 'Zapim Studio',
    [string]$AppPoolName = 'ZapimStudio',
    [string]$SitePath = 'C:\inetpub\zapim-studio',
    [string]$DataPath = 'C:\ZapimStudioData',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'ZapimStudio',
    # Windows account the GitHub Actions runner service uses; it needs to write the site folder.
    [string]$RunnerAccount = 'NT AUTHORITY\NETWORK SERVICE',
    [switch]$SkipDatabase
)
$ErrorActionPreference = 'Stop'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Grant($path) { icacls @args | Out-Null; if ($LASTEXITCODE -ne 0) { throw "icacls failed on $path (exit code $LASTEXITCODE)" } }

$SitePath = [IO.Path]::GetFullPath($SitePath)
$DataPath = [IO.Path]::GetFullPath($DataPath)
if ($DataPath.StartsWith($SitePath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DataPath must be outside SitePath: every deployment mirrors the site folder and would delete the data.'
}

Step 'Checking prerequisites'
$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path $ancm)) {
    throw 'The ASP.NET Core Module is missing. Install the .NET 10 Hosting Bundle (https://dotnet.microsoft.com/download/dotnet/10.0), run iisreset, then re-run this script.'
}
if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    if (-not (Get-WindowsFeature Web-AppInit).Installed) { Install-WindowsFeature Web-AppInit | Out-Null; Write-Host 'Installed IIS Application Initialization.' }
} else {
    Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -All -NoRestart | Out-Null
}
Import-Module WebAdministration

Step "Creating folders"
foreach ($dir in $SitePath, $DataPath, (Join-Path $DataPath 'assets'), (Join-Path $DataPath 'keys')) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

Step "Configuring app pool '$AppPoolName'"
$pool = "IIS:\AppPools\$AppPoolName"
if (-not (Test-Path $pool)) { New-WebAppPool -Name $AppPoolName | Out-Null }
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''
# The publishing scheduler runs inside the app: keep it running, never idle it out or recycle it on a timer,
# and never run two copies side by side.
Set-ItemProperty $pool -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name processModel.loadUserProfile -Value $true
Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name recycling.disallowOverlappingRotation -Value $true

Step "Configuring site '$SiteName' for $HostName"
if (-not (Get-Website -Name $SiteName)) {
    if ($CertificateThumbprint) {
        New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $AppPoolName -HostHeader $HostName -Port 443 -Ssl -SslFlags 1 | Out-Null
    } else {
        New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $AppPoolName -HostHeader $HostName -Port 80 | Out-Null
    }
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $SitePath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}
if ($CertificateThumbprint) {
    if (-not (Get-WebBinding -Name $SiteName -Protocol https -Port 443 -HostHeader $HostName)) {
        New-WebBinding -Name $SiteName -Protocol https -Port 443 -HostHeader $HostName -SslFlags 1
    }
    $binding = Get-WebBinding -Name $SiteName -Protocol https -Port 443 -HostHeader $HostName
    if ($binding.certificateHash) { $binding.RemoveSslCertificate() }
    $binding.AddSslCertificate($CertificateThumbprint, 'My')
} else {
    Write-Warning "No certificate given: the site is bound to http://$HostName only. Sign-in requires HTTPS, so add a certificate (re-run with -CertificateThumbprint) before using it."
}
Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true

Step 'Setting folder permissions'
$poolIdentity = "IIS AppPool\$AppPoolName"
Grant $SitePath $SitePath /grant "${poolIdentity}:(OI)(CI)RX" /grant "${RunnerAccount}:(OI)(CI)M"
# Database files, uploaded images and the encryption key ring: app pool and administrators only.
Grant $DataPath $DataPath /inheritance:r /grant:r "${poolIdentity}:(OI)(CI)M" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F'

Step 'Writing server configuration'
$settingsFile = Join-Path $SitePath 'appsettings.Production.json'
if (Test-Path $settingsFile) {
    Write-Host "$settingsFile already exists; leaving it unchanged."
} else {
    if (-not $BootstrapPassword) { $BootstrapPassword = Read-Host 'First administrator password (12+ characters)' -AsSecureString }
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($BootstrapPassword))
    if ($plain.Length -lt 12) { throw 'The administrator password must have at least 12 characters.' }
    $settings = [ordered]@{
        StoragePath       = $DataPath
        ConnectionStrings = [ordered]@{ SqlServer = "Server=$SqlServer;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True" }
        PublicBaseUrl     = "https://$HostName"
        Bootstrap         = [ordered]@{ Email = $BootstrapEmail; Password = $plain }
        AI                = [ordered]@{ ApiKey = '' }
        Publishing        = [ordered]@{ Enabled = $false }
    }
    $settings | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsFile -Encoding UTF8
    # Secrets: readable by the app pool, writable by administrators; the runner never needs it.
    Grant $settingsFile $settingsFile /inheritance:r /grant:r "${poolIdentity}:R" 'BUILTIN\Administrators:F' 'NT AUTHORITY\SYSTEM:F'
    Write-Host "Created $settingsFile"
}

if (-not $SkipDatabase) {
    Step "Creating SQL Server database '$Database'"
    if (Get-Command sqlcmd -ErrorAction SilentlyContinue) {
        sqlcmd -S $SqlServer -E -b -i (Join-Path $PSScriptRoot 'create-database.sql') -v "DatabaseName=$Database" "AppLogin=$poolIdentity"
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed. Run create-database.sql as a SQL Server administrator (see deploy/iis/README.md)." }
    } else {
        Write-Warning "sqlcmd not found. Run deploy\iis\create-database.sql in SSMS (SQLCMD Mode) with DatabaseName=$Database and AppLogin=$poolIdentity."
    }
}

Step 'Done'
Write-Host @"
Next steps:
  1. In GitHub (Settings > Secrets and variables > Actions > Variables) set
       ZAPIM_SITE_PATH  = $SitePath
       ZAPIM_HEALTH_URL = https://$HostName/health
  2. Run the 'Test and deploy' workflow (or push to main).
  3. Sign in as $BootstrapEmail, then remove Bootstrap.Password from
     $settingsFile and recycle the '$AppPoolName' app pool.
"@
