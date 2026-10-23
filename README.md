# Zapim Studio

An internal multi-brand marketing portal built with ASP.NET Core 10, EF Core (SQLite or SQL Server), and a responsive HTML/CSS/JavaScript interface. No Node build step is required.

## Included

- Separate brand workspaces, brand voice, audience, colour and logo.
- Cookie authentication; Admin, Approver and Editor roles; per-brand membership.
- Content studio with platform-specific captions, brand-aware AI copy, optional AI image generation, uploads, asset library and downloads. Each post chooses a visual: a brand-colour template, a photo with the headline, logo and website placed over it, an image posted as is, or no image for text-only LinkedIn and Facebook posts.
- **Plan with AI:** give a goal (or none), a start date, 1–4 weeks, posts per week and channels. The AI proposes a dated content calendar from the brand context: pillar, channel, day and local time, caption, creative headline and eyebrow, visual (brand graphic, AI photo or text only) and a one-line rationale per post. Everything is editable; selected posts become drafts with their creatives drawn automatically (AI photos optional, billed per image) and their suggested publish time, optionally sent straight to review. Approval is still required before scheduling, and the schedule dialog is prefilled with the suggested time.
- **Create with AI:** for a single post, the AI picks the topic if the brief is blank, writes the copy, chooses the visual and headline, generates the photo when it fits and recommends when to post.
- Draft → In review → Approved → Scheduled → Published workflow, change requests and audit history. Editing resets approval. Optimistic concurrency prevents stale edits or approvals.
- Monthly content calendar in the browser's local timezone, showing scheduled posts and planned drafts (dashed); server timestamps are UTC.
- Durable scheduler and adapters for LinkedIn text/image posts, Facebook Page text/image posts and Instagram professional-account single-image posts.
- Encrypted social tokens, account verification, expiry validation, publishing failure visibility and no automatic retry after uncertain delivery.
- Team management, operational reporting and CSV export. Reports contain real portal activity, not sample engagement numbers.

## Run locally

Install the .NET 10 SDK, then:

```bash
dotnet restore
dotnet run --launch-profile Zapim
```

Open **http://localhost:5080**. On a new development database, create your first administrator using the setup screen. There is no default password. Three clearly labelled starter drafts are seeded for Zapim. No accounts are connected and nothing is published automatically.

All records (unless SQL Server is configured), uploaded images and encryption keys are stored in `App_Data/` by default. This directory is ignored by Git. Keep backups of the entire directory: without the keys, saved social tokens cannot be decrypted.

## Configuration

Use environment variables or .NET user secrets; do not put keys in source control. Example:

```bash
dotnet user-secrets set 'AI:ApiKey' 'YOUR_OPENAI_KEY'
dotnet user-secrets set 'Bootstrap:Email' 'admin@example.com'
dotnet user-secrets set 'Bootstrap:Password' 'A_UNIQUE_LONG_PASSWORD'
```

| Environment variable | Purpose |
| --- | --- |
| `StoragePath` | Persistent directory containing SQLite, images and keys. |
| `ConnectionStrings__SqlServer` | Use SQL Server instead of SQLite. Point it at a dedicated, empty database; tables are created on first start. |
| `Bootstrap__Email`, `Bootstrap__Password` | Create the first administrator if no users exist. Required for first production startup; remove after initialization. Password minimum: 12 characters. |
| `AI__ApiKey` | Optional. The OpenAI key is normally entered by an administrator under **Settings**, which checks it with OpenAI and stores it encrypted in `openai.json` in the storage directory; this is used only until a key has been saved there. Without a key, copy generation is explicitly labelled as a starter template. |
| `AI__TextModel` | Default Responses API model, which **Settings** can override. Default: `gpt-4.1-mini`. |
| `AI__ImageModel` | Default image model supporting JPEG output, which **Settings** can override. Default: `gpt-image-1`. |
| `Metricool__UserToken`, `Metricool__UserId` | Optional. The Metricool account is normally entered by an administrator under **Settings**; these are used only until it has been saved there. |
| `PublicBaseUrl` | Public HTTPS origin, e.g. `https://studio.example.com`, required for Meta image publishing. |
| `Publishing__Enabled` | Default `false`. Set `true` only when live publication is intended. |
| `Publishing__MetaVersion` | Graph API version, default `v25.0`; align with the version supported by your Meta app. |
| `Publishing__LinkedInVersion` | LinkedIn API version, default `202610`; update before it sunsets. |
| `Publishing__MaxLatenessMinutes` | Default `60`. A scheduled post that the scheduler reaches later than this (after downtime, or while publishing was disabled) is not sent; it becomes **Needs attention** so it can be rescheduled. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | Set `true` only behind a reverse proxy, so client IPs (login rate limiting) and HTTPS (HSTS) are read from `X-Forwarded-*`. Already set in `compose.yaml` and `render.yaml`. Do not set it when the app is directly reachable. |

The creative builder runs in the browser and produces a 1080×1080 JPEG without an AI key. The photo layout crops the chosen upload, asset or AI image to a square and darkens it so the headline stays legible; AI images requested for this layout ask for no text in the picture. AI calls require an account with model access and incur provider charges. Generated images and copy remain drafts until reviewed.

## Social connections

### Through Metricool (recommended to start)

One Metricool account publishes for all brands. An administrator enters its user ID (the number after `userId=` in the Metricool address bar) and API token (Metricool → Account settings → API) under **Settings**. The token is encrypted with ASP.NET Core Data Protection into `metricool.json` in the storage directory and is never returned to the browser; leaving the token blank when saving keeps the current one. Metricool's API requires the Advanced or Custom plan.

Then, for each brand, an administrator selects the brand in **Accounts** and selects **Map Metricool brand**, then enters that brand's Metricool brand ID: the number after `blogId=` in the Metricool address bar. Each Zapim brand maps to one Metricool brand; the Metricool account and its token are shared by all brands. The mapping is checked against Metricool, and the card shows which of LinkedIn, Facebook and Instagram are connected to that Metricool brand.

While a brand is mapped, its direct connections below are not used. A post can be scheduled only if its network is connected to the Metricool brand. When a scheduled post is due, the scheduler sends it to Metricool's planner for publication two minutes later (Metricool accepts only future times), with the creative as a public `/media/...` link that Metricool copies. The post becomes **Published** once Metricool accepts it and returns a post ID; the audit log records "Sent to Metricool". Metricool then publishes it, and delivery problems after that point are reported in Metricool, not in Zapim Studio. Removing the mapping is blocked while the brand has scheduled posts.

### Direct platform tokens

An administrator selects a brand in **Accounts**, adds a token, account identifier, label and expiry, then selects **Verify**. Tokens are encrypted using ASP.NET Core Data Protection. The API never returns plaintext or encrypted tokens to clients.

This first version uses **manually provisioned platform tokens**, not an OAuth consent or automatic token-renewal flow. Verification tests access to the account; it does not establish that every publishing permission has been granted. The provider's publish response is authoritative.

- **LinkedIn:** supply an author URN (`urn:li:organization:...` or `urn:li:person:...`). Your app and token need appropriate `w_organization_social` or `w_member_social` access. Organization verification reads its organization resource; member verification uses OpenID userinfo and needs profile access. Image processing is checked before creating the post.
- **Facebook:** supply a Page ID and Page token with the necessary Page publishing permissions, including `pages_manage_posts`. Personal-profile posting is not supported.
- **Instagram:** uses the Instagram API with Facebook Login. Supply the professional-account ID and appropriately authorized token. The account must be linked to a Facebook Page. Typical scopes include `instagram_basic`, `instagram_content_publish` and required Page permissions. JPEG images only in this version.

Meta retrieves approved media from `/media/{random-asset-id}`. Only assets referenced by scheduled, publishing, published or uncertain-delivery posts are exposed there, and only when `PublicBaseUrl` is set to HTTPS. Draft uploads and logos remain authenticated. Approved campaign images must therefore be appropriate for public retrieval when queued.

The hosted worker checks due posts every 15 seconds and claims each post atomically. Posts more than `Publishing__MaxLatenessMinutes` overdue are not published. Replacing an account token clears its verification; scheduled posts for that account will not publish until it is verified again. A post becomes **Published** only after a platform response includes a post identifier. Interrupted/uncertain attempts become **Needs attention**. Check the actual social profile before duplicating a replacement draft, to avoid duplicate posts. There is no automatic retry of a publish call.

## Deploy

### Free Render demo

The included `render.yaml` declares a free Docker web service with a `/health` check and publishing disabled. Connect the repository in Render using **New → Blueprint**, select the `main` branch, and enter `Bootstrap__Email` and `Bootstrap__Password` when prompted. The password must contain at least 12 characters. Use the resulting HTTPS address to sign in.

This is a disposable demo: Render Free has no persistent disk and sleeps after inactivity. Brands, drafts, uploads, team accounts and encryption keys reset when the container is replaced. Keep the bootstrap settings on this demo so your administrator account is recreated after a reset. Do not rely on this deployment for unattended scheduled publishing.

### Windows Server with IIS and SQL Server

See [deploy/iis/README.md](deploy/iis/README.md): a one-time setup script for IIS and SQL Server, a Visual Studio Web Deploy profile for the `zapimmarketing` site, and optionally a GitHub Actions workflow that tests and deploys every push to `main` through a self-hosted runner.

### Persistent hosting

Run on a .NET-capable host (Azure App Service, Linux service, IIS or Docker). The Sites plugin's JavaScript Worker hosting cannot execute this ASP.NET Core app.

```bash
dotnet publish -c Release -o ./publish
```

For Docker, set `BOOTSTRAP_EMAIL`, `BOOTSTRAP_PASSWORD`, and optionally `OPENAI_API_KEY`, `PUBLIC_BASE_URL`, `PUBLISHING_ENABLED` in your deployment environment, then run:

```bash
docker compose up --build -d
```

The container listens on port 8080; Compose binds it to `127.0.0.1:5080`. Put a TLS-terminating reverse proxy in front of it. Production cookies require HTTPS. Use the named persistent volume for `/data` and back up the SQLite database together with assets and keys. Filesystem access to `/data` should be restricted to the application identity. Keys are persisted on disk; use the host's protected storage or a managed key store for additional at-rest protection.

**Deploy one application instance.** SQLite and startup recovery are intended for one instance. Scaling requires a shared database, shared key ring and a distributed job/lease design. Production setup does not expose a public first-admin registration form. Do not run the Development environment on an internet-facing deployment.

The initial schema is created with `EnsureCreated`. Schema changes need an explicit migration plan before replacing an existing production deployment.

## Verification

```bash
dotnet build
node --check wwwroot/app.js
python3 tests/smoke.py
```

The smoke suite creates a temporary database and runs the compiled application on a local port. Set `TEST_SQLSERVER` and `TEST_SQLCMD` to run it against SQL Server instead of SQLite (see the top of `tests/smoke.py`); CI runs both. It exercises authentication, anti-CSRF enforcement, brand isolation, role checks, draft/review/approval transitions, stale updates, approval invalidation, disabled publishing and durable records. It never calls external social APIs. See `tests/smoke.py` for prerequisites and overrides.

## Current boundaries

- Single-image and text publishing only. Carousel/document/video publishing and campaign batch generation are not implemented.
- Tokens are provisioned manually. OAuth app onboarding, refresh-token rotation, email invitations, password-reset email and SSO are not implemented.
- Engagement/attribution data is not fetched. Reports cover portal workflow and confirmed publishing only.
- The app needs configured AI credentials and social app access before those external features can be tested end to end. No social posts were sent during development.
- User-supplied JPEG/PNG signatures and file size are checked; a production media pipeline should additionally decode, normalize and strip metadata from uploaded images.
- The interface follows zapim.com: royal blue `#0038C9`, the site's blue button gradient and violet-to-pink accent, Plus Jakarta Sans headings and Inter body text (self-hosted in `wwwroot/fonts`, SIL Open Font License). The starter Zapim brand is created with context written from zapim.com (`Services/ZapimContext.cs`), including the published figures the AI may quote; edit it under Brands at any time. Vesper Telecom and Welco Infotech are added the same way from their websites (`Services/BrandPresets.cs`): each is created once at startup if no brand with its website or name exists, and is never recreated or overwritten.

## API references

- [LinkedIn Posts API](https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api)
- [LinkedIn Images API](https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/images-api)
- [Meta's Instagram API collection](https://www.postman.com/meta/instagram/documentation/6yqw8pt/instagram-api)
- [OpenAI Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [OpenAI image generation](https://developers.openai.com/api/docs/guides/image-generation)
