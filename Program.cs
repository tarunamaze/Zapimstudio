using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Zapim.Marketing;

var builder = WebApplication.CreateBuilder(args);
var storage = Path.GetFullPath(builder.Configuration["StoragePath"] ?? "App_Data");
Directory.CreateDirectory(storage);
Directory.CreateDirectory(Path.Combine(storage, "assets"));
builder.Services.AddSingleton(new Storage(storage));
// SQL Server when a connection string is configured; otherwise a SQLite file in the storage directory.
var sqlServer = builder.Configuration.GetConnectionString("SqlServer");
builder.Services.AddDbContext<MarketingDb>(o =>
{
    if (string.IsNullOrWhiteSpace(sqlServer)) o.UseSqlite($"Data Source={Path.Combine(storage, "marketing.db")}");
    else o.UseSqlServer(sqlServer);
});
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(storage, "keys"))).SetApplicationName("ZapimMarketing");
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddSingleton<IPasswordHasher<TeamUser>, PasswordHasher<TeamUser>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "zapim.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<MarketingDb>();
        var user = await db.Users.FindAsync(int.Parse(ctx.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!));
        if (user is null || !user.Active || user.Role != ctx.Principal!.FindFirstValue(ClaimTypes.Role)) ctx.RejectPrincipal();
    };
});
builder.Services.AddAuthorization();
// Reject missing or null JSON fields as 400s instead of failing later with a NullReferenceException.
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.RespectNullableAnnotations = true; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("ai", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anon", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddHttpClient("external", c => c.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddScoped<ContentService>();
builder.Services.AddScoped<Publisher>();
builder.Services.AddScoped<MetricoolClient>();
builder.Services.AddSingleton<MetricoolSettings>();
builder.Services.AddHostedService<PublishingWorker>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MarketingDb>();
    await db.Database.EnsureCreatedAsync();
    if (!await db.Users.AnyAsync() && builder.Configuration["Bootstrap:Email"] is { Length: > 0 } email)
    {
        var password = builder.Configuration["Bootstrap:Password"] ?? "";
        Rules.Require(password.Length >= 12, "Bootstrap password must have at least 12 characters.");
        var user = new TeamUser { Name = "Administrator", Email = email.Trim().ToLowerInvariant(), Role = "Admin" };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<TeamUser>>().HashPassword(user, password);
        db.Users.Add(user); await db.SaveChangesAsync();
    }
    if (!await db.Brands.AnyAsync())
    {
        var brand = new Brand { Name = "Zapim", Website = "https://zapim.com", Color = "#087f76", Audience = "Marketing, customer experience and product leaders at Indian businesses", Description = "Business communication through WhatsApp, SMS, RCS, voice and email. Campaigns, support and transactional messaging in one platform.", Tone = "Confident, helpful, practical. Explain business outcomes clearly; avoid unsupported statistics." };
        db.Brands.Add(brand); await db.SaveChangesAsync();
        db.Posts.AddRange(
            new Post { BrandId = brand.Id, Title = "One conversation. Every channel.", Platform = "LinkedIn", Caption = "Your customers move between channels. Your team should be able to follow the conversation.\n\nExplore WhatsApp, SMS, RCS, voice and email with Zapim.\n\nSee how it works: https://zapim.com/request-demo\n\n#CustomerExperience #BusinessMessaging #Zapim", ReviewNote = "Starter draft — review product claims before publishing." },
            new Post { BrandId = brand.Id, Title = "Turn questions into conversations", Platform = "Instagram", Caption = "A product question is the start of a conversation. Make it easy for shoppers to get an answer on WhatsApp.\n\nDiscover Zapim at the link in your bio.\n\n#WhatsAppBusiness #Ecommerce #Zapim", ReviewNote = "Starter draft — add a creative and review before publishing." },
            new Post { BrandId = brand.Id, Title = "A better way to follow up", Platform = "Facebook", Caption = "Keep customers informed with timely order updates and helpful follow-ups. Discover business messaging with Zapim.\n\nhttps://zapim.com/request-demo", ReviewNote = "Starter draft — review before publishing." });
        await db.SaveChangesAsync();
    }
    // Never silently retry an interrupted publish: the remote service may have accepted it.
    await db.Posts.Where(x => x.Status == "Publishing").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "NeedsAttention").SetProperty(x => x.Error, "Publishing was interrupted. Check the social profile for duplicates before creating a replacement draft."));
}
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (RuleException e) { ctx.Response.StatusCode = e.Status; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (DbUpdateConcurrencyException) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "This post changed. Refresh and try again." }); }
    catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Your session token expired. Refresh this page." }); }
    catch (BadHttpRequestException e) when (!ctx.Response.HasStarted) { ctx.Response.StatusCode = e.StatusCode; await ctx.Response.WriteAsJsonAsync(new { error = "The request was incomplete or invalid. Refresh and try again." }); }
    catch (Exception e) when (!ctx.Response.HasStarted)
    {
        app.Logger.LogError("Request failed: {Type}", e.GetType().Name);
        ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "The operation could not be completed. Please try again." });
    }
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseDefaultFiles(); app.UseStaticFiles();
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method))
        await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
    await next();
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/session", async (HttpContext ctx, MarketingDb db, IAntiforgery csrf, MetricoolSettings metricoolSettings) => new
{
    csrfToken = csrf.GetAndStoreTokens(ctx).RequestToken,
    setupRequired = !await db.Users.AnyAsync(),
    setupAllowed = app.Environment.IsDevelopment(),
    user = ctx.User.Identity?.IsAuthenticated == true ? await db.Users.FindAsync(Rules.UserId(ctx)) : null,
    aiEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["AI:ApiKey"]),
    publishingEnabled = builder.Configuration.GetValue<bool>("Publishing:Enabled"),
    publicMediaConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["PublicBaseUrl"]),
    metricoolConfigured = metricoolSettings.Configured
});
app.MapPost("/api/setup", async (LoginInput input, MarketingDb db, IPasswordHasher<TeamUser> hasher) =>
{
    Rules.Require(app.Environment.IsDevelopment(), "Set Bootstrap__Email and Bootstrap__Password on the server to create the first administrator.", 403);
    // Transaction serializes first-admin creation across concurrent requests.
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
    Rules.Require(!await db.Users.AnyAsync(), "Setup is already complete.", 409);
    Rules.ValidateLogin(input);
    var user = new TeamUser { Name = "Administrator", Email = input.Email.Trim().ToLowerInvariant(), Role = "Admin" };
    user.PasswordHash = hasher.HashPassword(user, input.Password); db.Users.Add(user);
    await db.SaveChangesAsync(); await tx.CommitAsync(); return Results.Ok();
}).RequireRateLimiting("login");
app.MapPost("/api/login", async (LoginInput input, HttpContext ctx, MarketingDb db, IPasswordHasher<TeamUser> hasher) =>
{
    var user = await db.Users.SingleOrDefaultAsync(x => x.Email == input.Email.Trim().ToLowerInvariant());
    Rules.Require(user is not null && user.Active && hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password) != PasswordVerificationResult.Failed, "Email or password is incorrect.", 401);
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user!.Id.ToString()), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Role, user.Role) }, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok();
}).RequireRateLimiting("login");
app.MapPost("/api/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/brands", async (HttpContext ctx, MarketingDb db) => await Rules.Brands(ctx, db).OrderBy(x => x.Name).ToListAsync());
api.MapPost("/brands", async (BrandInput input, HttpContext ctx, MarketingDb db) =>
{
    Rules.Admin(ctx); var brand = new Brand(); Rules.ApplyBrand(brand, input); db.Brands.Add(brand); await db.SaveChangesAsync(); return brand;
});
api.MapPut("/brands/{brandId:int}", async (int brandId, BrandInput input, HttpContext ctx, MarketingDb db) =>
{
    Rules.Admin(ctx); var brand = await Rules.Brand(ctx, db, brandId); Rules.ApplyBrand(brand, input);
    if (input.LogoAssetId.Length > 0) Rules.Require(await db.Assets.AnyAsync(x => x.Id == input.LogoAssetId && x.BrandId == brandId), "Choose a logo belonging to this brand.");
    brand.LogoAssetId = input.LogoAssetId;
    await db.SaveChangesAsync(); return brand;
});
api.MapGet("/posts", async (HttpContext ctx, MarketingDb db) =>
{
    var ids = Rules.Brands(ctx, db).Select(x => x.Id); return await db.Posts.Where(x => ids.Contains(x.BrandId)).OrderByDescending(x => x.UpdatedAt).ToListAsync();
});
api.MapPost("/brands/{brandId:int}/posts", async (int brandId, PostInput input, HttpContext ctx, MarketingDb db) =>
{
    await Rules.Brand(ctx, db, brandId); await Rules.ValidatePost(input, brandId, db);
    var post = new Post { BrandId = brandId, CreatedBy = Rules.UserId(ctx) }; Rules.ApplyPost(post, input); db.Posts.Add(post);
    await db.SaveChangesAsync(); db.Audit.Add(new AuditEntry { BrandId = brandId, PostId = post.Id, UserId = Rules.UserId(ctx), Action = "Draft created" }); await db.SaveChangesAsync(); return post;
});
api.MapPut("/posts/{id:int}", async (int id, PostInput input, HttpContext ctx, MarketingDb db) =>
{
    var post = await Rules.Post(ctx, db, id); Rules.Require(post.Status is "Draft" or "ChangesRequested" or "InReview" or "Approved", "Return this post to draft before editing.", 409);
    Rules.Require(input.Version == post.Version, "This post changed. Refresh before saving.", 409);
    await Rules.ValidatePost(input, post.BrandId, db); Rules.ApplyPost(post, input); post.Status = "Draft"; post.ApprovedBy = null; post.Version++; post.UpdatedAt = DateTime.UtcNow;
    db.Audit.Add(new AuditEntry { BrandId = post.BrandId, PostId = id, UserId = Rules.UserId(ctx), Action = "Edited; approval reset" }); await db.SaveChangesAsync(); return post;
});
api.MapPost("/posts/{id:int}/transition", async (int id, TransitionInput input, HttpContext ctx, MarketingDb db, MetricoolClient metricool, MetricoolSettings metricoolSettings) =>
{
    var post = await Rules.Post(ctx, db, id); Rules.Require(input.Version == post.Version, "This post changed. Refresh and try again.", 409);
    var old = post.Status;
    switch (input.Action)
    {
        case "review": Rules.Require(old is "Draft" or "ChangesRequested", "Only drafts can be submitted."); post.Status = "InReview"; break;
        case "approve": Rules.Approver(ctx); Rules.Require(old == "InReview", "Submit the post for review first."); post.Status = "Approved"; post.ApprovedBy = Rules.UserId(ctx); break;
        case "changes": Rules.Approver(ctx); Rules.Require(old == "InReview", "Post is not in review."); Rules.Require(!string.IsNullOrWhiteSpace(input.Note) && input.Note.Length <= 2000, "Add a review note (up to 2,000 characters)."); post.Status = "ChangesRequested"; post.ReviewNote = input.Note; break;
        case "draft": Rules.Require(old is "InReview" or "Approved" or "Scheduled" or "ChangesRequested", "This post cannot return to draft."); post.Status = "Draft"; post.ApprovedBy = null; post.ScheduledAt = null; break;
        case "schedule":
        case "publish":
            Rules.Approver(ctx); Rules.Require(old == "Approved", "Only approved posts can be published.");
            Rules.Require(builder.Configuration.GetValue<bool>("Publishing:Enabled"), "Live publishing is disabled. Enable Publishing__Enabled on the server after configuring accounts.");
            var account = await Publisher.Destination(db, metricoolSettings, post.BrandId, post.Platform);
            var scheduled = input.Action == "publish" ? DateTime.UtcNow : input.ScheduledAt?.ToUniversalTime();
            Rules.Require(scheduled.HasValue && (input.Action == "publish" || scheduled > DateTime.UtcNow), "Choose a future date and time.");
            Rules.Require(account is not null && account.VerifiedAt.HasValue && account.ExpiresAt > scheduled, "Map this brand to Metricool, or connect and verify an account with a token valid through the publish time.");
            var viaMetricool = account!.Platform == MetricoolClient.Platform;
            if (viaMetricool) Rules.Require((await metricool.Brand(account.ExternalId)).Networks.Contains(post.Platform), $"Connect {post.Platform} to the Metricool brand '{account.Name}' first.");
            Rules.Require(post.Platform != "Instagram" || post.AssetId.Length > 0, "Instagram requires an image.");
            if (post.AssetId.Length > 0 && (viaMetricool || post.Platform is "Instagram" or "Facebook")) Rules.Require(Rules.PublicUrl(builder.Configuration), $"Configure an HTTPS PublicBaseUrl so {(viaMetricool ? "Metricool" : "Meta")} can retrieve the creative.");
            if (post.Platform == "Instagram") Rules.Require((await db.Assets.FindAsync(post.AssetId))?.ContentType == "image/jpeg", "Instagram requires a JPEG creative. Use the creative builder or upload a JPEG.");
            post.Status = "Scheduled"; post.ScheduledAt = scheduled; break;
        case "archive": Rules.Require(old is not "Publishing" and not "Scheduled", "Cancel scheduling before archiving."); post.Status = "Archived"; break;
        default: throw new RuleException("Unknown action.");
    }
    post.Version++; post.UpdatedAt = DateTime.UtcNow;
    db.Audit.Add(new AuditEntry { BrandId = post.BrandId, PostId = id, UserId = Rules.UserId(ctx), Action = $"{old} → {post.Status}" });
    await db.SaveChangesAsync(); return post;
});
api.MapGet("/brands/{brandId:int}/accounts", async (int brandId, HttpContext ctx, MarketingDb db) => { await Rules.Brand(ctx, db, brandId); return await db.Accounts.Where(x => x.BrandId == brandId).ToListAsync(); });
api.MapPost("/brands/{brandId:int}/accounts", async (int brandId, AccountInput input, HttpContext ctx, MarketingDb db, IDataProtectionProvider protection) =>
{
    Rules.Admin(ctx); await Rules.Brand(ctx, db, brandId); Rules.Platform(input.Platform);
    Rules.Require(input.Name.Length is > 0 and <= 100 && input.Token.Length is > 10 and < 8000 && input.ExpiresAt > DateTime.UtcNow, "Provide a label, valid token and future expiry.");
    Rules.Require(input.Platform == "LinkedIn" ? System.Text.RegularExpressions.Regex.IsMatch(input.ExternalId, "^urn:li:(organization|person):[A-Za-z0-9_-]+$") : System.Text.RegularExpressions.Regex.IsMatch(input.ExternalId, "^[0-9]+$"), "Use a LinkedIn author URN or numeric Meta account ID.");
    var account = await db.Accounts.SingleOrDefaultAsync(x => x.BrandId == brandId && x.Platform == input.Platform) ?? new SocialAccount { BrandId = brandId, Platform = input.Platform };
    if (account.Id == 0) db.Accounts.Add(account);
    account.Name = input.Name; account.ExternalId = input.ExternalId; account.ExpiresAt = input.ExpiresAt.ToUniversalTime(); account.VerifiedAt = null;
    account.ProtectedToken = protection.CreateProtector("social-tokens-v1").Protect(input.Token);
    await db.SaveChangesAsync(); return account;
});
object MetricoolStatus(MetricoolSettings s) => new { configured = s.Configured, source = s.Source, userId = s.Current?.UserId ?? "", updatedAt = s.UpdatedAt };
api.MapGet("/settings/metricool", (HttpContext ctx, MetricoolSettings settings) => { Rules.Admin(ctx); return MetricoolStatus(settings); });
api.MapPut("/settings/metricool", (MetricoolSettingsInput input, HttpContext ctx, MetricoolSettings settings) =>
{
    Rules.Admin(ctx); settings.Save(input.UserId.Trim(), input.Token);
    app.Logger.LogInformation("Metricool settings changed by user {UserId}", Rules.UserId(ctx));
    return MetricoolStatus(settings);
});
api.MapDelete("/settings/metricool", (HttpContext ctx, MetricoolSettings settings) => { Rules.Admin(ctx); settings.Clear(); return MetricoolStatus(settings); });
api.MapGet("/brands/{brandId:int}/metricool", async (int brandId, HttpContext ctx, MarketingDb db, MetricoolClient metricool, MetricoolSettings settings) =>
{
    await Rules.Brand(ctx, db, brandId);
    var configured = settings.Configured;
    var account = await db.Accounts.SingleOrDefaultAsync(x => x.BrandId == brandId && x.Platform == MetricoolClient.Platform);
    if (!configured || account is null) return Results.Ok(new { configured, account, networks = (string[]?)null, error = "" });
    try { return Results.Ok(new { configured, account, networks = (string[]?)(await metricool.Brand(account.ExternalId)).Networks, error = "" }); }
    catch (RuleException e) { return Results.Ok(new { configured, account, networks = (string[]?)null, error = e.Message }); }
});
api.MapPut("/brands/{brandId:int}/metricool", async (int brandId, MetricoolInput input, HttpContext ctx, MarketingDb db, MetricoolClient metricool, MetricoolSettings settings) =>
{
    Rules.Admin(ctx); await Rules.Brand(ctx, db, brandId);
    Rules.Require(settings.Configured, "Add the Metricool user ID and API token under Settings first.");
    Rules.Require(System.Text.RegularExpressions.Regex.IsMatch(input.BlogId, "^[0-9]{1,18}$"), "Enter the numeric Metricool brand ID (the blogId in the Metricool address bar).");
    var remote = await metricool.Brand(input.BlogId);
    var account = await db.Accounts.SingleOrDefaultAsync(x => x.BrandId == brandId && x.Platform == MetricoolClient.Platform) ?? new SocialAccount { BrandId = brandId, Platform = MetricoolClient.Platform };
    if (account.Id == 0) db.Accounts.Add(account);
    // No per-brand token: the server-wide Metricool token is used. Mapping is verified against Metricool just above.
    account.ExternalId = remote.BlogId; account.Name = remote.Label; account.ExpiresAt = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc); account.VerifiedAt = DateTime.UtcNow;
    db.Audit.Add(new AuditEntry { BrandId = brandId, UserId = Rules.UserId(ctx), Action = $"Mapped to Metricool brand {remote.Label}" });
    await db.SaveChangesAsync(); return new { configured = true, account, networks = (string[]?)remote.Networks, error = "" };
});
api.MapPost("/accounts/{id:int}/verify", async (int id, HttpContext ctx, MarketingDb db, Publisher publisher) =>
{
    Rules.Admin(ctx); var account = await db.Accounts.FindAsync(id) ?? throw new RuleException("Account not found.", 404); await Rules.Brand(ctx, db, account.BrandId);
    await publisher.Verify(account); account.VerifiedAt = DateTime.UtcNow; await db.SaveChangesAsync(); return account;
});
api.MapDelete("/accounts/{id:int}", async (int id, HttpContext ctx, MarketingDb db) =>
{
    Rules.Admin(ctx); var account = await db.Accounts.FindAsync(id) ?? throw new RuleException("Account not found.", 404); await Rules.Brand(ctx, db, account.BrandId);
    Rules.Require(!await db.Posts.AnyAsync(x => x.BrandId == account.BrandId && (account.Platform == MetricoolClient.Platform || x.Platform == account.Platform) && (x.Status == "Scheduled" || x.Status == "Publishing")), "Cancel scheduled posts before disconnecting this account.");
    db.Accounts.Remove(account); await db.SaveChangesAsync(); return Results.Ok();
});
api.MapPost("/brands/{brandId:int}/assets", async (int brandId, HttpContext ctx, MarketingDb db, Storage files) =>
{
    await Rules.Brand(ctx, db, brandId); var form = await ctx.Request.ReadFormAsync(); var file = form.Files.FirstOrDefault();
    Rules.Require(file is not null && file.Length is > 0 and <= 5_000_000, "Upload a JPEG or PNG under 5 MB.");
    using var ms = new MemoryStream(); await file!.CopyToAsync(ms);
    return await files.Save(db, brandId, ms.ToArray(), Path.GetFileName(file.FileName));
});
api.MapGet("/brands/{brandId:int}/assets", async (int brandId, HttpContext ctx, MarketingDb db) => { await Rules.Brand(ctx, db, brandId); return await db.Assets.Where(x => x.BrandId == brandId).OrderByDescending(x => x.CreatedAt).ToListAsync(); });
api.MapGet("/assets/{id}", async (string id, HttpContext ctx, MarketingDb db, Storage files) =>
{
    var asset = await db.Assets.FindAsync(id) ?? throw new RuleException("Asset not found.", 404); await Rules.Brand(ctx, db, asset.BrandId); return Results.File(files.AssetPath(asset.Id), asset.ContentType);
});
app.MapGet("/media/{id}", async (string id, MarketingDb db, Storage files) =>
{
    Rules.Require(Rules.PublicUrl(builder.Configuration) && await db.Posts.AnyAsync(x => x.AssetId == id && (x.Status == "Scheduled" || x.Status == "Publishing" || x.Status == "Published" || x.Status == "NeedsAttention")), "Not found.", 404);
    var asset = await db.Assets.FindAsync(id) ?? throw new RuleException("Not found.", 404); return Results.File(files.AssetPath(asset.Id), asset.ContentType);
});
api.MapPost("/brands/{brandId:int}/generate", async (int brandId, GenerateInput input, HttpContext ctx, MarketingDb db, ContentService service) =>
{
    var brand = await Rules.Brand(ctx, db, brandId); Rules.Platform(input.Platform); Rules.Require(input.Brief.Length is > 5 and <= 4000, "Enter a brief between 6 and 4,000 characters."); return await service.Generate(brand, input);
}).RequireRateLimiting("ai");
api.MapPost("/brands/{brandId:int}/generate-image", async (int brandId, GenerateInput input, HttpContext ctx, MarketingDb db, ContentService service) =>
{
    var brand = await Rules.Brand(ctx, db, brandId); Rules.Require(input.Brief.Length is > 5 and <= 4000, "Enter a creative brief between 6 and 4,000 characters."); return await service.Image(brand, input.Brief);
}).RequireRateLimiting("ai");
api.MapGet("/team", async (HttpContext ctx, MarketingDb db) => { Rules.Admin(ctx); return await db.Users.Include(x => x.Memberships).ToListAsync(); });
api.MapPost("/team", async (UserInput input, HttpContext ctx, MarketingDb db, IPasswordHasher<TeamUser> hasher) =>
{
    Rules.Admin(ctx); Rules.ValidateLogin(new LoginInput(input.Email, input.Password)); Rules.Require(input.Role is "Admin" or "Editor" or "Approver", "Choose a valid role.");
    Rules.Require(input.Name.Length is > 0 and <= 100, "Enter a name.");
    var email = input.Email.Trim().ToLowerInvariant(); Rules.Require(!await db.Users.AnyAsync(x => x.Email == email), "This email already has an account.");
    var ids = input.BrandIds.Distinct().ToArray(); Rules.Require(await db.Brands.CountAsync(x => ids.Contains(x.Id)) == ids.Length, "Unknown brand.");
    var user = new TeamUser { Name = input.Name, Email = email, Role = input.Role, Memberships = ids.Select(id => new Membership { BrandId = id }).ToList() };
    user.PasswordHash = hasher.HashPassword(user, input.Password); db.Users.Add(user); await db.SaveChangesAsync(); return user;
});
api.MapPut("/team/{id:int}", async (int id, AccessInput input, HttpContext ctx, MarketingDb db) =>
{
    Rules.Admin(ctx); Rules.Require(id != Rules.UserId(ctx), "Ask another administrator to change your own access.");
    var user = await db.Users.Include(x => x.Memberships).SingleOrDefaultAsync(x => x.Id == id) ?? throw new RuleException("User not found.", 404);
    Rules.Require(input.Role is "Admin" or "Editor" or "Approver", "Choose a valid role.");
    var ids = input.BrandIds.Distinct().ToArray(); Rules.Require(await db.Brands.CountAsync(x => ids.Contains(x.Id)) == ids.Length, "Unknown brand.");
    user.Role = input.Role; user.Active = input.Active;
    db.Memberships.RemoveRange(user.Memberships.Where(m => !ids.Contains(m.BrandId)));
    foreach (var brandId in ids.Where(i => !user.Memberships.Any(m => m.BrandId == i))) user.Memberships.Add(new Membership { UserId = id, BrandId = brandId });
    await db.SaveChangesAsync(); return user;
});
api.MapGet("/audit", async (HttpContext ctx, MarketingDb db) => { var ids = Rules.Brands(ctx, db).Select(x => x.Id); return await db.Audit.Where(x => ids.Contains(x.BrandId)).OrderByDescending(x => x.At).Take(100).ToListAsync(); });
app.Map("/api/{**path}", () => Results.NotFound(new { error = "API endpoint not found." }));
app.MapFallbackToFile("index.html");
app.Run();

public record LoginInput(string Email, string Password);
public record BrandInput(string Name, string Website, string Color, string Tone, string Audience, string Description, string LogoAssetId = "");
public record PostInput(string Title, string Platform, string Caption, string AssetId = "", int Version = 0);
public record TransitionInput(string Action, int Version, DateTime? ScheduledAt = null, string Note = "");
public record MetricoolInput(string BlogId);
public record MetricoolSettingsInput(string UserId, string Token = "");
public record AccountInput(string Platform, string Name, string ExternalId, string Token, DateTime ExpiresAt);
public record GenerateInput(string Brief, string Platform = "LinkedIn");
public record UserInput(string Name, string Email, string Password, string Role, int[] BrandIds);
public record AccessInput(string Role, bool Active, int[] BrandIds);
