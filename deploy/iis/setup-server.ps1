#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time IIS setup for Zapim Studio on Windows Server. Safe to re-run.

.DESCRIPTION
    Creates the app pool, site, data folder and server-only configuration, and (if sqlcmd is installed)
    the SQL Server database. Deployments themselves are done by Web Deploy from Visual Studio, or by
    deploy.ps1 from GitHub Actions.

    If the site already exists in IIS, its physical path, app pool and host name are used unless given.
    A site that shares its app pool with another site is moved to a dedicated pool named after the site.

.EXAMPLE
    .\setup-server.ps1 -BootstrapEmail admin@example.com -WebDeployUser 'EUNEW\hrmsdeploy'

.EXAMPLE
    .\setup-server.ps1 -HostName studio.example.com -BootstrapEmail admin@example.com -CertificateThumbprint 0123ABCD...
#>
param(
    [string]$HostName = '',
    [Parameter(Mandatory)][string]$BootstrapEmail,
    [securestring]$BootstrapPassword,
    [string]$CertificateThumbprint = '',
    [string]$SiteName = 'zapimmarketing',
    [string]$AppPoolName = '',
    [string]$SitePath = '',
    [string]$DataPath = 'C:\ZapimMarketingData',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'ZapimMarketing',
    # Windows account the GitHub Actions runner service uses; it needs to write the site folder.
    [string]$RunnerAccount = 'NT AUTHORITY\NETWORK SERVICE',
    # Windows account or IIS Manager user that publishes with Web Deploy (WMSVC); granted this site only.
    [string]$WebDeployUser = '',
    [switch]$SkipDatabase
)
$ErrorActionPreference = 'Stop'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Grant($path) { icacls @args | Out-Null; if ($LASTEXITCODE -ne 0) { throw "icacls failed on $path (exit code $LASTEXITCODE)" } }
function IsWindowsAccount($name) { try { ([Security.Principal.NTAccount]$name).Translate([Security.Principal.SecurityIdentifier]) | Out-Null; $true } catch { $false } }

Step 'Checking prerequisites'
$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
$runtimes = foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App') {
    Get-ChildItem (Join-Path $env:ProgramFiles "dotnet\shared\$framework") -Directory -Filter '10.*' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not (Test-Path $ancm) -or @($runtimes).Count -lt 2) {
    throw 'The .NET 10 Hosting Bundle is missing. Install it (https://dotnet.microsoft.com/download/dotnet/10.0), run iisreset, then re-run this script. It installs side by side with older .NET versions; iisreset restarts every site on the server for a few seconds.'
}
if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    if (-not (Get-WindowsFeature Web-AppInit).Installed) { Install-WindowsFeature Web-AppInit | Out-Null; Write-Host 'Installed IIS Application Initialization.' }
} else {
    Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -All -NoRestart | Out-Null
}
Import-Module WebAdministration

$existing = Get-Website -Name $SiteName
if ($existing) {
    Write-Host "Site '$SiteName' already exists; using its settings where none were given."
    if (-not $SitePath) { $SitePath = [Environment]::ExpandEnvironmentVariables($existing.physicalPath) }
    if (-not $HostName) { $HostName = @($existing.bindings.Collection | ForEach-Object { $_.bindingInformation.Split(':')[2] } | Where-Object { $_ })[0] }
    if (-not $AppPoolName) {
        $AppPoolName = $existing.applicationPool
        if (Get-Website | Where-Object { $_.applicationPool -eq $AppPoolName -and $_.name -ne $SiteName }) {
            Write-Host "App pool '$AppPoolName' is shared with another site; '$SiteName' gets its own pool '$SiteName'."
            $AppPoolName = $SiteName
        }
    }
} elseif (-not $HostName) {
    throw "Site '$SiteName' does not exist yet: pass -HostName so it can be created."
}
if (-not $SitePath) { $SitePath = "C:\inetpub\$SiteName" }
if (-not $AppPoolName) { $AppPoolName = $SiteName }

$SitePath = [IO.Path]::GetFullPath($SitePath)
$DataPath = [IO.Path]::GetFullPath($DataPath)
if ($DataPath.StartsWith($SitePath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DataPath must be outside SitePath: every deployment mirrors the site folder and would delete the data.'
}

Step "Creating folders"
foreach ($dir in $SitePath, $DataPath, (Join-Path $DataPath 'assets'), (Join-Path $DataPath 'keys')) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

Step "Configuring app pool '$AppPoolName'"
$pool = "IIS:\AppPools\$AppPoolName"
if (-not (Test-Path $pool)) { New-WebAppPool -Name $AppPoolName | Out-Null }
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''
# The Hosting Bundle's runtime is 64-bit; a 32-bit pool fails with HTTP 500.31.
Set-ItemProperty $pool -Name enable32BitAppOnWin64 -Value $false
# The publishing scheduler runs inside the app: keep it running, never idle it out or recycle it on a timer,
# and never run two copies side by side.
Set-ItemProperty $pool -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name processModel.loadUserProfile -Value $true
Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name recycling.disallowOverlappingRotation -Value $true

Step "Configuring site '$SiteName' for $HostName"
if (-not $existing) {
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
    if (-not $HostName) { throw '-CertificateThumbprint needs -HostName for the HTTPS binding.' }
    if (-not (Get-WebBinding -Name $SiteName -Protocol https -Port 443 -HostHeader $HostName)) {
        New-WebBinding -Name $SiteName -Protocol https -Port 443 -HostHeader $HostName -SslFlags 1
    }
    $binding = Get-WebBinding -Name $SiteName -Protocol https -Port 443 -HostHeader $HostName
    if ($binding.certificateHash) { $binding.RemoveSslCertificate() }
    $binding.AddSslCertificate($CertificateThumbprint, 'My')
} elseif (-not (Get-WebBinding -Name $SiteName -Protocol https)) {
    Write-Warning "Site '$SiteName' has no HTTPS binding. Sign-in requires HTTPS, so add a certificate (re-run with -HostName and -CertificateThumbprint) before using it."
}
Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true

Step 'Setting folder permissions'
$poolIdentity = "IIS AppPool\$AppPoolName"
Grant $SitePath $SitePath /grant "${poolIdentity}:(OI)(CI)RX" /grant "${RunnerAccount}:(OI)(CI)M"
if ($WebDeployUser) {
    # A Windows account publishes with its own file rights; an IIS Manager user publishes through the WMSVC identity.
    if (IsWindowsAccount $WebDeployUser) { Grant $SitePath $SitePath /grant "${WebDeployUser}:(OI)(CI)M" }
    $mgmt = Join-Path $env:windir 'system32\inetsrv\Microsoft.Web.Management.dll'
    if (Test-Path $mgmt) {
        Add-Type -Path $mgmt
        [Microsoft.Web.Management.Server.ManagementAuthorization]::Grant($WebDeployUser, $SiteName, $false) | Out-Null
        Write-Host "Granted Web Deploy user '$WebDeployUser' -> site '$SiteName'."
    } else {
        Write-Warning 'IIS Management Service (WMSVC) is not installed; skipped the Web Deploy permission.'
    }
}
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
        PublicBaseUrl     = $(if ($HostName) { "https://$HostName" } else { '' })
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
        # Passed as environment variables, which sqlcmd reads as scripting variables: '-v AppLogin=...' breaks on the
        # space in the login name.
        $env:DatabaseName = $Database; $env:AppLogin = $poolIdentity
        try { sqlcmd -S $SqlServer -E -C -b -i (Join-Path $PSScriptRoot 'create-database.sql') }
        finally { Remove-Item Env:DatabaseName, Env:AppLogin }
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed. Run create-database.sql as a SQL Server administrator (see deploy/iis/README.md)." }
    } else {
        Write-Warning "sqlcmd not found. Run deploy\iis\create-database.sql in SSMS (SQLCMD Mode) with DatabaseName=$Database and AppLogin=$poolIdentity."
    }
}

Step 'Done'
Write-Host @"
Next steps:
  1. Publish: in Visual Studio use the 'zapimmarketing' publish profile, or in GitHub
     (Settings > Secrets and variables > Actions > Variables) set
       ZAPIM_SITE_PATH  = $SitePath
       ZAPIM_HEALTH_URL = https://$HostName/health
     and run the 'Test and deploy' workflow.
  2. Sign in as $BootstrapEmail, then remove Bootstrap.Password from
     $settingsFile and recycle the '$AppPoolName' app pool.
  3. Back up the '$Database' database and $DataPath together.
"@
