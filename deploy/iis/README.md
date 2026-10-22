# Deploying to Windows Server with IIS and SQL Server

The app runs as the IIS site **`zapimmarketing`** on **169.58.88.219**, next to HRMS, with its own SQL Server database **`ZapimMarketing`**. It is published the same way as HRMS: Web Deploy from Visual Studio over `https://169.58.88.219:8172/msdeploy.axd`. A GitHub Actions workflow with a self-hosted runner is available as an alternative ([below](#alternative-deploy-from-github-actions)).

What lives where on the server:

| Location | Contents | Changed by deployments? |
| --- | --- | --- |
| The `zapimmarketing` site folder (e.g. `C:\inetpub\zapimmarketing`) | The published app and `web.config` | Yes |
| `appsettings.Production.json` in the site folder | Connection string, admin bootstrap, publishing settings | No, server-only, never in Git |
| `C:\ZapimMarketingData` | Uploaded images (`assets`), the encryption key ring (`keys`), and the Metricool and OpenAI settings saved under **Settings** (encrypted) | No |
| SQL Server database `ZapimMarketing` | All records | No (tables are created on first start) |

## 1. Prerequisites (once)

1. **.NET 10 Hosting Bundle.** HRMS uses .NET 8, so the server most likely does not have it yet. Download it from <https://dotnet.microsoft.com/download/dotnet/10.0> (*ASP.NET Core Runtime → Windows → Hosting Bundle*), install it, then run `iisreset`. It installs side by side and does not change HRMS, but `iisreset` restarts every site, HRMS included, for a few seconds: do it outside working hours.
2. **A host name and HTTPS certificate.** **Sign-in only works over HTTPS**: the session cookie is marked Secure in production, so an HTTP-only or bare-IP site shows the sign-in page but never signs in. The host name is **`zapimmarketing.duckdns.org`**:
   1. In [DuckDNS](https://www.duckdns.org), point the `zapimmarketing` subdomain at 169.58.88.219. Check with `nslookup zapimmarketing.duckdns.org`.
   2. Make sure the `zapimmarketing` site has an HTTP binding on port 80 for `zapimmarketing.duckdns.org` (IIS Manager → site → Bindings), and port 80 is open in the firewall: Let's Encrypt validates over HTTP.
   3. Get a free certificate with [win-acme](https://www.win-acme.com/) (`wacs.exe` → *Create certificate (default settings)* → pick the `zapimmarketing` site). It adds the HTTPS binding and renews automatically. Note the certificate's thumbprint: `Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -like '*zapimmarketing*'`.
3. **SQL Server** on the same machine: the default instance on port 1433 that HRMS uses is fine. `sqlcmd` is already installed (the HRMS migration scripts use it).

## 2. Prepare the server (once)

Copy the `deploy\iis` folder to the server. In an **elevated Windows PowerShell** there:

```powershell
cd deploy\iis
.\setup-server.ps1 -BootstrapEmail you@zapim.com `
                   -HostName zapimmarketing.duckdns.org `
                   -CertificateThumbprint <THUMBPRINT>
```

It prompts for the first administrator password (12+ characters), then:

- uses the existing `zapimmarketing` site and its physical path. If that site shares its app pool with another site (e.g. `DefaultAppPool`), it gets its own `zapimmarketing` pool, so settings below never affect HRMS.
- configures the app pool to run always, with no idle timeout, no timed recycling and no overlapping recycles, because the publishing scheduler runs inside the app.
- adds an HTTPS binding for the host name with your certificate, and enables preload.
- creates `C:\ZapimMarketingData`, readable only by the app pool and administrators.
- writes `appsettings.Production.json` into the site folder. The connection string uses Windows authentication as the app pool identity, so no database password is stored. To use a SQL login instead (this server uses `sa`), see [SQL login](#sql-login).
- with `-WebDeployUser`, lets a non-administrator Web Deploy account publish to this site. Not needed when publishing as `Administrator`.
- **creates the database:** runs `create-database.sql`, which creates the empty `ZapimMarketing` database and a login for `IIS APPPOOL\zapimmarketing` with `db_ddladmin`, `db_datareader` and `db_datawriter` on that database only. It connects with your Windows account, which must be a SQL Server administrator. It does not touch the HRMS databases.

Without `sqlcmd`, open `create-database.sql` in SSMS, switch on **Query → SQLCMD Mode**, add these two lines at the top, and run it:

```sql
:setvar DatabaseName ZapimMarketing
:setvar AppLogin "IIS APPPOOL\zapimmarketing"
```

The script is safe to re-run. It never overwrites an existing `appsettings.Production.json`. Without `-HostName` it reads the host name from the site's existing bindings; without `-CertificateThumbprint` it warns if the site has no HTTPS binding. Run `Get-Help .\setup-server.ps1 -Full` for every option (paths, names, `-SqlServer localhost,1433` or a named instance).

### SQL login

The server's database connection uses `sa`. This writes the connection string into `appsettings.Production.json` without displaying the password, then restarts the app (elevated PowerShell on the server):

```powershell
$f='C:\inetpub\wwwroot\zapimmarketing\appsettings.Production.json'; $b=New-Object System.Data.SqlClient.SqlConnectionStringBuilder; $b['Data Source']='localhost'; $b['Initial Catalog']='ZapimMarketing'; $b['User ID']='sa'; $b['Password']=[Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR((Read-Host 'sa password' -AsSecureString))); $b['TrustServerCertificate']=$true; $j=Get-Content $f -Raw | ConvertFrom-Json; $j.ConnectionStrings.SqlServer=$b.ConnectionString; $j | ConvertTo-Json -Depth 5 | Set-Content $f -Encoding UTF8; Restart-WebAppPool zapimmarketing
```

A dedicated login with rights on `ZapimMarketing` only (like HRMS's `hrms_app`) is safer than `sa`: replace `sa` above with that login.

> **Use a dedicated database.** The app creates its tables only when its database is empty. Do not point it at an HRMS database.

## 3. Publish from Visual Studio

Open the project, then **Build → Publish → zapimmarketing** (the profile is in `Properties\PublishProfiles\zapimmarketing.pubxml`) and enter the server's `Administrator` password when prompted. Accept the self-signed certificate on port 8172, as for HRMS.

The profile never overwrites `appsettings.Production.json` or the `logs` folder, and takes the site offline (`app_offline.htm`) while copying. Publishing from the command line works too:

```powershell
dotnet publish Zapim.Marketing.csproj -c Release /p:PublishProfile=zapimmarketing /p:Password=<password>
```

## 4. First sign-in

On first start the app creates its tables in `ZapimMarketing` and your administrator account. Then:

1. Check `https://<host name>/health` returns `{"status":"ok"}`, and sign in.
2. Remove the `Password` line under `Bootstrap` in `appsettings.Production.json` and recycle the `zapimmarketing` app pool.
3. Connect Metricool (one account for all brands): sign in as an administrator, open **Settings**, and enter the Metricool user ID and API token. Then in **Accounts**, map each brand to its Metricool brand ID.
4. Optional: for AI captions and images, enter an OpenAI API key under **Settings**.
5. Set `Publishing.Enabled` to `true` in `appsettings.Production.json` when you are ready for scheduled posts to go out, then recycle the app pool.

## Day-to-day

- **Backups:** back up the `ZapimMarketing` database **and** `C:\ZapimMarketingData`. Without the `keys` folder, saved social tokens, the Metricool token and the OpenAI key cannot be decrypted and must be re-entered. Add the database to whatever backs up the HRMS databases.
- **Run one instance only.** Don't add the site to a web farm or a second server.
- **Schema changes:** tables are created with `EnsureCreated`, which never alters existing tables. Future model changes need a migration plan before deploying.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| HTTP 500.31 / 500.30 right after the first publish | The .NET 10 Hosting Bundle is missing, or `iisreset` was not run after installing it. |
| HTTP 500.30 / health check fails | Event Viewer → Windows Logs → Application, source *IIS AspNetCore Module V2*. For more detail set `stdoutLogEnabled="true"` in the site's `web.config` temporarily (it is replaced on the next publish). |
| `Login failed for user 'IIS APPPOOL\zapimmarketing'` | `create-database.sql` was not run, or was run against another instance or database name. If the site's pool has another name, the login must match it: re-run `setup-server.ps1`. |
| Publish fails with 401 / `ERROR_USER_UNAUTHORIZED` | Wrong password, or a non-administrator deploy account without permission on `zapimmarketing`: re-run `setup-server.ps1` with `-WebDeployUser`. |
| `UnauthorizedAccessException` for `...\zapimmarketing\App_Data` | `appsettings.Production.json` is missing, so the app falls back to the site folder: run `setup-server.ps1`. |
| HTTP 500.31 just after installing .NET 10 | The app pool still runs the process that failed before: `Restart-WebAppPool zapimmarketing`. |
| Sign-in does nothing / returns to the sign-in page | The site is being reached over HTTP or by IP address. Use the HTTPS host name. |

## Alternative: deploy from GitHub Actions

Instead of publishing from Visual Studio, every push to `main` (or a manual run of **Test and deploy** in the Actions tab) can run the smoke tests on GitHub-hosted runners against SQLite and SQL Server, then publish and copy the app into the site from a self-hosted runner on the server. The deploy job is skipped until the `ZAPIM_SITE_PATH` variable is set.

```
push to main ─▶ test (ubuntu, SQLite + SQL Server) ─▶ deploy (your server's runner) ─▶ IIS site ─▶ /health check
```

1. **Register a runner:** GitHub → this repository → **Settings → Actions → Runners → New self-hosted runner → Windows**. Extract into its own folder (e.g. `C:\actions-runner-zapim`), accept the default labels and answer **Y** to *run as a service*. The service runs as `NT AUTHORITY\NETWORK SERVICE` by default; if you choose another account, pass it to `setup-server.ps1` as `-RunnerAccount`.
2. **Set variables:** GitHub → **Settings → Secrets and variables → Actions → Variables**:

   | Variable | Example |
   | --- | --- |
   | `ZAPIM_SITE_PATH` | `C:\inetpub\zapimmarketing` |
   | `ZAPIM_HEALTH_URL` | `https://zapimmarketing.duckdns.org/health` |

   No secrets are needed in GitHub: all credentials stay in `appsettings.Production.json` on the server.

Each deploy takes the site offline for a few seconds (`app_offline.htm`), mirrors the new build and waits for `/health`. If copying fails the offline page stays up, so a half-copied build is never served; fix the cause and re-run the workflow. A queued deploy job means the runner is offline (`Get-Service actions.runner.*`); `Access to the path ... is denied` means the runner account lacks write access to the site folder.

**This repository is public.** The workflow never runs on pull requests, so code from forks cannot reach your server. Don't add `pull_request` triggers to workflows that use the self-hosted runner. Making the repository private is the safer option.
