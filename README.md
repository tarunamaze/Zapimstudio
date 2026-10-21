# Zapim Studio

An internal multi-brand marketing portal built with ASP.NET Core 10, EF Core/SQLite, and a responsive HTML/CSS/JavaScript interface. No Node build step is required.

## Included

- Separate brand workspaces, brand voice, audience, colour and logo.
- Cookie authentication; Admin, Approver and Editor roles; per-brand membership.
- Content studio with platform-specific captions, brand-aware AI copy, an editable square creative builder, optional AI image generation, uploads, asset library and downloads.
- Draft → In review → Approved → Scheduled → Published workflow, change requests and audit history. Editing resets approval. Optimistic concurrency prevents stale edits or approvals.
- Monthly content calendar in the browser's local timezone; server timestamps are UTC.
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

All records, uploaded images and encryption keys are stored in `App_Data/` by default. This directory is ignored by Git. Keep backups of the entire directory: without the keys, saved social tokens cannot be decrypted.

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
| `Bootstrap__Email`, `Bootstrap__Password` | Create the first administrator if no users exist. Required for first production startup; remove after initialization. Password minimum: 12 characters. |
| `AI__ApiKey` | Enables OpenAI copy and image generation. Without it, copy generation is explicitly labelled as a starter template. |
| `AI__TextModel` | Configurable Responses API model. Default: `gpt-4.1-mini`. |
| `AI__ImageModel` | Configurable image model supporting JPEG output. Default: `gpt-image-1`. |
| `PublicBaseUrl` | Public HTTPS origin, e.g. `https://studio.example.com`, required for Meta image publishing. |
| `Publishing__Enabled` | Default `false`. Set `true` only when live publication is intended. |
| `Publishing__MetaVersion` | Graph API version, default `v25.0`; align with the version supported by your Meta app. |
| `Publishing__LinkedInVersion` | LinkedIn API version, default `202610`; update before it sunsets. |
| `Publishing__MaxLatenessMinutes` | Default `60`. A scheduled post that the scheduler reaches later than this (after downtime, or while publishing was disabled) is not sent; it becomes **Needs attention** so it can be rescheduled. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | Set `true` only behind a reverse proxy, so client IPs (login rate limiting) and HTTPS (HSTS) are read from `X-Forwarded-*`. Already set in `compose.yaml` and `render.yaml`. Do not set it when the app is directly reachable. |

The creative builder runs in the browser and produces a 1080×1080 JPEG without an AI key. AI calls require an account with model access and incur provider charges. Generated images and copy remain drafts until reviewed.

## Social connections

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

The smoke suite creates a temporary database and runs the compiled application on a local port. It exercises authentication, anti-CSRF enforcement, brand isolation, role checks, draft/review/approval transitions, stale updates, approval invalidation, disabled publishing and durable records. It never calls external social APIs. See `tests/smoke.py` for prerequisites and overrides.

## Current boundaries

- Single-image and text publishing only. Carousel/document/video publishing and campaign batch generation are not implemented.
- Tokens are provisioned manually. OAuth app onboarding, refresh-token rotation, email invitations, password-reset email and SSO are not implemented.
- Engagement/attribution data is not fetched. Reports cover portal workflow and confirmed publishing only.
- The app needs configured AI credentials and social app access before those external features can be tested end to end. No social posts were sent during development.
- User-supplied JPEG/PNG signatures and file size are checked; a production media pipeline should additionally decode, normalize and strip metadata from uploaded images.
- Brand colour is configurable. The initial design is a proposed Zapim Studio visual identity; substitute your approved brand assets and typography as needed.

## API references

- [LinkedIn Posts API](https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api)
- [LinkedIn Images API](https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/images-api)
- [Meta's Instagram API collection](https://www.postman.com/meta/instagram/documentation/6yqw8pt/instagram-api)
- [OpenAI Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [OpenAI image generation](https://developers.openai.com/api/docs/guides/image-generation)
