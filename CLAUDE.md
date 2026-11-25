# Zapim Studio — project brief

Internal multi-brand marketing portal: AI plans, writes and designs social posts; people review and approve; posts publish
through Metricool or direct platform tokens. ASP.NET Core 10 minimal API (`Program.cs`), EF Core on SQL Server (SQLite
locally), plain HTML/CSS/JS front end in `wwwroot` (no build step). See `README.md` for features and configuration.

## Live deployment
- URL: https://zapimmarketing.duckdns.org (DuckDNS, Let's Encrypt via win-acme). Sign-in only works over HTTPS.
- Server: Windows, IIS site `zapimmarketing` on 169.58.88.219 (same box as HRMS and SalaryBridge). Site folder
  `C:\inetpub\wwwroot\zapimmarketing`, data folder `C:\ZapimMarketingData` (assets, Data Protection `keys`, and the
  encrypted `metricool.json`, `openai.json`, `link-tracking.json` settings). Back up the data folder with the database.
- Database: SQL Server default instance, database `ZapimMarketing`, connection as `sa` (set in
  `appsettings.Production.json` in the site folder; never in Git).
- First admin: `admin@zapim.com` (password set on the server by the owner; there is no password-reset flow yet).
- `Publishing.Enabled` is `false` until the team is ready for scheduled posts to go out.

## Deploying
- Publish with `.\deploy\iis\publish.ps1`: Web Deploy as `Administrator` to https://169.58.88.219:8172 using
  `Properties/PublishProfiles/zapimmarketing.pubxml`; it prompts for the password and checks `/health`. Run it in a
  terminal the user can type into, and let the user type the password; never ask for it or handle it.
- `web.config` in the project removes IIS WebDAV (otherwise PUT/DELETE return 405 on the server). Keep it.
- Static files are served with `Cache-Control: no-cache`, so a normal reload picks up a new version.
- After publishing, check the live site serves the change (e.g. `curl` `/app.js` for a new function name).
- One-time server setup is `deploy/iis/setup-server.ps1` (see `deploy/iis/README.md`); already done on this server.

## Git
- Repo `tarunamaze/Zapimstudio`; work branch and default branch: `claude/eager-ritchie-rhccnc`.
- The GitHub login used here lacks the `workflow` scope, so commits touching `.github/workflows/` are rejected on push.
  Keep the workflow commit last and push `HEAD~1`, or run `gh auth refresh -h github.com -s workflow` first.
  One local-only commit ("Skip the self-hosted deploy job until ZAPIM_SITE_PATH is set") may not be on GitHub yet.

## Verifying changes
- `dotnet build`, `node --check wwwroot/app.js`, `python3 tests/smoke.py` (73 checks). The smoke test runs the app on
  a temporary SQLite database with a local stand-in server for Metricool and OpenAI; it never calls real services.
- For UI checks, run the built dll with `AI__BaseUrl` pointing at a local stand-in for OpenAI and drive the browser.

## How things work (non-obvious)
- Schema is created with `EnsureCreated`, which never alters existing tables: avoid schema changes. New data has gone
  into existing columns (e.g. a draft's suggested publish time is `Post.ScheduledAt` while not Scheduled; a brand's
  Metricool mapping is a `SocialAccount` with Platform `Metricool`) or into encrypted JSON files in the storage
  folder (`SavedSettings<T>`).
- Brand context: `Services/ZapimContext.cs` (Zapim, written from zapim.com) and `Services/BrandPresets.cs`
  (Vesper Telecom, Welco Infotech). Created once at startup, never recreated or overwritten. They list only facts each
  website states; the AI is told never to invent statistics, customers or prices.
- AI: `Services/ContentService.cs` (OpenAI Responses API with strict JSON schemas; plans over weeks or on specific
  dates, single posts, carousels, SEO articles, repurposing a page). The server re-validates everything the AI returns
  (channels, dates, layouts, times).
- `Services/PageFetcher.cs` reads only https pages on the brand's own host, from public IPs checked at connect time.
- Channels: LinkedIn, Instagram, Facebook, and Google (Google Business Profile, Metricool only). LinkedIn carousels are
  PDFs built in the browser (`makePdf` in `wwwroot/app.js`).
- UTM tags are added in `Publisher` when a post is sent (`Services/LinkTracking.cs`); stored captions stay clean.
- Theme follows zapim.com: `#0038C9`, blue gradient buttons, violet-pink accents, Plus Jakarta Sans + Inter
  (self-hosted in `wwwroot/fonts`).

## Open items
- Not yet tried live: a LinkedIn carousel and a Google Business post through Metricool; plans and articles with the
  real OpenAI key.
- Upload each brand's logo (Brands); remove `Bootstrap.Password` from the server's `appsettings.Production.json`.
- welcoinfotech.com has an expired HTTPS certificate and leftover template text.
- Ideas raised but not built: password reset, engagement analytics, video posts, Instagram carousels.

## Related work
- The zapim.com website source is on another PC (more than 30 MB). Website changes are separate from this app; this
  app's Blog & SEO tab only produces articles to paste into the website.
