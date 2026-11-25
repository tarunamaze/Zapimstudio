<#
.SYNOPSIS
    Publishes Zapim Studio to the zapimmarketing IIS site (169.58.88.219) with Web Deploy, then checks /health.

.DESCRIPTION
    Uses the zapimmarketing publish profile (Properties\PublishProfiles\zapimmarketing.pubxml, user Administrator).
    The password is typed here and passed to MSBuild through an environment variable for this run only; it is
    never written to disk. Requires the .NET 10 SDK and Web Deploy (msdeploy) on this PC.

.EXAMPLE
    .\deploy\iis\publish.ps1
#>
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\..\Zapim.Marketing.csproj'
$secure = Read-Host 'Password for Administrator on 169.58.88.219' -AsSecureString
$env:Password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
try {
    dotnet publish $project -c Release -p:PublishProfile=zapimmarketing -v:minimal
    $code = $LASTEXITCODE
} finally {
    Remove-Item Env:Password
}
if ($code -ne 0) { Write-Host "PUBLISH FAILED (exit code $code)" -ForegroundColor Red; return }
Write-Host 'PUBLISHED. Checking https://zapimmarketing.duckdns.org/health ...' -ForegroundColor Green
try { $r = Invoke-WebRequest https://zapimmarketing.duckdns.org/health -UseBasicParsing -TimeoutSec 90; "HEALTH $($r.StatusCode) $($r.Content)" }
catch { "HEALTH FAILED: $($_.Exception.Message)" }
