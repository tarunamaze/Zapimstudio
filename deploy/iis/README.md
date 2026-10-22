# Deploying to Windows Server with IIS and SQL Server

Every push to `main` (or a manual run of **Test and deploy** in the Actions tab) runs the smoke tests on GitHub-hosted runners against both SQLite and SQL Server, then a self-hosted runner on your server publishes the app and copies it into the IIS site.

```
push to main ─▶ test (ubuntu, SQLite + SQL Server) ─▶ deploy (your server's runner) ─▶ IIS site ─▶ /health check
```

What lives where on the server:

| Location | Contents | Changed by deployments? |
| --- | --- | --- |
| `C:\inetpub\zapim-studio` | The published app and `web.config` | Yes, mirrored on every deploy |
| `C:\inetpub\zapim-studio\appsettings.Production.json` | Connection string, admin bootstrap, AI key, publishing settings | No, server-only, never in Git |
| `C:\ZapimStudioData` | Uploaded images (`assets`) and the encryption key ring (`keys`) | No |
| SQL Server database `ZapimStudio` | All records | No (tables are created on first start) |

## 1. Prerequisites (once)

1. **.NET 10 Hosting Bundle.** Download it from <https://dotnet.microsoft.com/download/dotnet/10.0> (under *ASP.NET Core Runtime → Windows → Hosting Bundle*), install it, then run `iisreset`.
2. **DNS:** an `A` record for your subdomain (e.g. `studio.yourcompany.com`) pointing at the server.
3. **HTTPS certificate** for that subdomain in the machine's *Personal* (`My`) certificate store. A corporate certificate works, or a free one from Let's Encrypt via [win-acme](https://www.win-acme.com/). Note its thumbprint (`Get-ChildItem Cert:\LocalMachine\My`). **Sign-in only works over HTTPS**: the session cookie is marked Secure in production.
4. **SQL Server 2016 or newer** on the same machine (any edition, including Express).

## 2. A runner for this repository

GitHub runners on a personal account belong to one repository, so the runner that deploys your other app cannot pick up this repository's jobs. Register a second runner on the same server:

1. GitHub → this repository → **Settings → Actions → Runners → New self-hosted runner → Windows**.
2. Follow the commands shown there, but extract into a **new folder** (e.g. `C:\actions-runner-zapim`) so the existing runner is untouched.
3. When `config.cmd` asks, accept the default labels and answer **Y** to *run as a service*. The service runs as `NT AUTHORITY\NETWORK SERVICE` by default; if you choose another account, pass it to the setup script as `-RunnerAccount`.

**This repository is public.** The workflow never runs on pull requests, so code from forks cannot reach your server. To keep it that way, don't add `pull_request` triggers to workflows that use the self-hosted runner. Making the repository private is the safer option.

## 3. Prepare the server (once)

In an **elevated Windows PowerShell** on the server, from a checkout or download of this repository:

```powershell
cd deploy\iis
.\setup-server.ps1 -HostName studio.yourcompany.com `
                   -BootstrapEmail you@yourcompany.com `
                   -CertificateThumbprint <THUMBPRINT>
```

It prompts for the first administrator password (12+ characters), then:

- creates the `ZapimStudio` app pool. It is always running, with no idle timeout, no timed recycling and no overlapping recycles, because the publishing scheduler runs inside the app.
- creates the `Zapim Studio` IIS site with an HTTPS binding for your hostname and preload enabled.
- creates `C:\ZapimStudioData`, readable only by the app pool and administrators.
- writes `appsettings.Production.json` into the site folder. The connection string uses Windows authentication as the app pool identity, so no database password is stored.
- grants the runner account write access to the site folder.
- runs `create-database.sql` if `sqlcmd` is installed: it creates the `ZapimStudio` database and a login for `IIS APPPOOL\ZapimStudio` with `db_ddladmin`, `db_datareader` and `db_datawriter` on that database only.

The database step connects with your Windows account, which must be a SQL Server administrator. Without `sqlcmd`, open `create-database.sql` in SSMS, switch on **Query → SQLCMD Mode**, add these two lines at the top, and run it:

```sql
:setvar DatabaseName ZapimStudio
:setvar AppLogin "IIS APPPOOL\ZapimStudio"
```

The script is safe to re-run. It never overwrites an existing `appsettings.Production.json`. Run `Get-Help .\setup-server.ps1 -Full` for every option (paths, names, SQL instance such as `-SqlServer .\SQLEXPRESS`).

> **Use a dedicated database.** The app creates its tables only when its database is empty. Do not point it at your other application's database.

## 4. Tell the workflow where to deploy

GitHub → **Settings → Secrets and variables → Actions → Variables → New repository variable**:

| Variable | Example |
| --- | --- |
| `ZAPIM_SITE_PATH` | `C:\inetpub\zapim-studio` |
| `ZAPIM_HEALTH_URL` | `https://studio.yourcompany.com/health` |

No secrets are needed in GitHub: all credentials stay in `appsettings.Production.json` on the server.

## 5. First deployment

Merge to `main`, or run **Actions → Test and deploy → Run workflow**. On first start the app creates its tables and your administrator account. Then:

1. Sign in at `https://studio.yourcompany.com`.
2. Remove the `Password` line under `Bootstrap` in `appsettings.Production.json` and recycle the `ZapimStudio` app pool.
3. Optional: add `AI.ApiKey`, and set `Publishing.Enabled` to `true` once social accounts are connected (see the main README). Recycle the app pool after each change.

## Day-to-day

- **Deploys** take the site offline for a few seconds (`app_offline.htm`), mirror the new build, and wait for `/health`. If copying fails the offline page stays up, so a half-copied build is never served; fix the cause and re-run the workflow.
- **Backups:** back up the `ZapimStudio` database **and** `C:\ZapimStudioData`. Without the `keys` folder, saved social tokens cannot be decrypted and must be re-entered.
- **Run one instance only.** Don't add the site to a web farm or a second server.
- **Schema changes:** tables are created with `EnsureCreated`, which never alters existing tables. Future model changes need a migration plan before deploying.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Deploy job stays *Queued* | The runner for **this** repository is offline: `Get-Service actions.runner.*`. |
| `Access to the path ... is denied` during deploy | The runner account lacks write access to the site folder: re-run `setup-server.ps1` with the right `-RunnerAccount`. |
| HTTP 500.30 / health check fails | Event Viewer → Windows Logs → Application, source *IIS AspNetCore Module V2*. For more detail set `stdoutLogEnabled="true"` in the site's `web.config` temporarily (it is replaced on the next deploy). |
| `Login failed for user 'IIS APPPOOL\ZapimStudio'` | `create-database.sql` was not run, or was run against another instance or database name. |
| Sign-in does nothing / returns to the sign-in page | The site is being reached over HTTP. Use the HTTPS address. |
