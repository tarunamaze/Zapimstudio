using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Zapim.Marketing;
public class Publisher(IHttpClientFactory factory, IDataProtectionProvider protection, IConfiguration config, Storage storage, MarketingDb db)
{
    private HttpClient Client => factory.CreateClient("external");
    private string Token(SocialAccount a) => protection.CreateProtector("social-tokens-v1").Unprotect(a.ProtectedToken);
    private string Graph => $"https://graph.facebook.com/{config["Publishing:MetaVersion"]}";
    private HttpRequestMessage Request(HttpMethod method, string url, SocialAccount a, bool linkedIn = false)
    {
        var r = new HttpRequestMessage(method, url); r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(a));
        if (linkedIn) { r.Headers.Add("LinkedIn-Version", config["Publishing:LinkedInVersion"]); r.Headers.Add("X-Restli-Protocol-Version", "2.0.0"); }
        return r;
    }
    private async Task<JsonDocument> Json(HttpRequestMessage r, CancellationToken ct = default)
    {
        using (r)
        using (var response = await Client.SendAsync(r, ct))
        {
            Rules.Require(response.IsSuccessStatusCode, $"Platform returned HTTP {(int)response.StatusCode}. Check account permissions, token expiry, API version and media requirements. Check the profile before retrying.", 502);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }
    public async Task Verify(SocialAccount a)
    {
        Rules.Require(a.ExpiresAt > DateTime.UtcNow, "This access token has expired.");
        string url;
        if (a.Platform == "LinkedIn")
            url = a.ExternalId.StartsWith("urn:li:organization:") ? "https://api.linkedin.com/rest/organizations/" + a.ExternalId.Split(':').Last() : "https://api.linkedin.com/v2/userinfo";
        else url = $"{Graph}/{a.ExternalId}?fields=id,name";
        using var result = await Json(Request(HttpMethod.Get, url, a, a.Platform == "LinkedIn"));
        if (a.Platform == "LinkedIn" && a.ExternalId.StartsWith("urn:li:person:"))
            Rules.Require(result.RootElement.TryGetProperty("sub", out var sub) && sub.GetString() == a.ExternalId.Split(':').Last(), "The LinkedIn token does not match this member URN.");
    }
    public async Task<string> Publish(Post post, SocialAccount a, CancellationToken ct)
    {
        Rules.Require(a.ExpiresAt > DateTime.UtcNow, "Account token expired. Reconnect the account.");
        // Replacing a token clears VerifiedAt; never publish with a token nobody has verified.
        Rules.Require(a.VerifiedAt.HasValue, "The account token was replaced and has not been verified. Verify the account, then reschedule this post.");
        var image = post.AssetId.Length > 0 ? await db.Assets.FindAsync([post.AssetId], ct) : null;
        var mediaUrl = $"{config["PublicBaseUrl"]?.TrimEnd('/')}/media/{post.AssetId}";
        if (a.Platform == "LinkedIn")
        {
            string? imageUrn = null;
            if (image is not null)
            {
                var init = Request(HttpMethod.Post, "https://api.linkedin.com/rest/images?action=initializeUpload", a, true);
                init.Content = JsonContent.Create(new { initializeUploadRequest = new { owner = a.ExternalId } });
                using var initialized = await Json(init, ct);
                var value = initialized.RootElement.GetProperty("value"); imageUrn = value.GetProperty("image").GetString();
                var uri = new Uri(value.GetProperty("uploadUrl").GetString()!);
                Rules.Require(uri.Scheme == "https" && (uri.Host.EndsWith(".linkedin.com") || uri.Host.EndsWith(".licdn.com")), "Unexpected LinkedIn upload destination.");
                using var upload = Request(HttpMethod.Put, uri.AbsoluteUri, a);
                upload.Content = new ByteArrayContent(await File.ReadAllBytesAsync(storage.AssetPath(image.Id), ct));
                upload.Content.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
                using var uploaded = await Client.SendAsync(upload, ct); Rules.Require(uploaded.IsSuccessStatusCode, "LinkedIn image upload failed.", 502);
                var available = false;
                for (var i = 0; i < 12; i++)
                {
                    using var status = await Json(Request(HttpMethod.Get, "https://api.linkedin.com/rest/images/" + Uri.EscapeDataString(imageUrn!), a, true), ct);
                    var state = status.RootElement.GetProperty("status").GetString();
                    if (state == "AVAILABLE") { available = true; break; }
                    Rules.Require(state != "PROCESSING_FAILED", "LinkedIn could not process this image."); await Task.Delay(1500, ct);
                }
                Rules.Require(available, "LinkedIn image processing timed out.");
            }
            var body = new Dictionary<string, object> { ["author"] = a.ExternalId, ["commentary"] = post.Caption, ["visibility"] = "PUBLIC", ["distribution"] = new { feedDistribution = "MAIN_FEED", targetEntities = Array.Empty<string>(), thirdPartyDistributionChannels = Array.Empty<string>() }, ["lifecycleState"] = "PUBLISHED", ["isReshareDisabledByAuthor"] = false };
            if (imageUrn is not null) body["content"] = new { media = new { id = imageUrn, title = post.Title } };
            using var request = Request(HttpMethod.Post, "https://api.linkedin.com/rest/posts", a, true); request.Content = JsonContent.Create(body);
            using var response = await Client.SendAsync(request, ct);
            Rules.Require(response.IsSuccessStatusCode, $"LinkedIn returned HTTP {(int)response.StatusCode}. Check the profile before retrying.", 502);
            Rules.Require(response.Headers.TryGetValues("x-restli-id", out var ids), "LinkedIn returned no post ID. Check the profile before retrying.");
            return ids!.First();
        }
        if (a.Platform == "Facebook")
        {
            var request = Request(HttpMethod.Post, $"{Graph}/{a.ExternalId}/{(image is null ? "feed" : "photos")}", a);
            request.Content = new FormUrlEncodedContent(image is null ? new Dictionary<string, string> { ["message"] = post.Caption } : new Dictionary<string, string> { ["caption"] = post.Caption, ["url"] = mediaUrl, ["published"] = "true" });
            using var result = await Json(request, ct); return result.RootElement.TryGetProperty("post_id", out var p) ? p.GetString()! : result.RootElement.GetProperty("id").GetString()!;
        }
        Rules.Require(image is not null && image.ContentType == "image/jpeg", "Instagram requires a JPEG image.");
        var containerRequest = Request(HttpMethod.Post, $"{Graph}/{a.ExternalId}/media", a);
        containerRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["image_url"] = mediaUrl, ["caption"] = post.Caption });
        using var container = await Json(containerRequest, ct); var containerId = container.RootElement.GetProperty("id").GetString();
        var ready = false;
        for (var i = 0; i < 20; i++)
        {
            using var status = await Json(Request(HttpMethod.Get, $"{Graph}/{containerId}?fields=status_code", a), ct);
            var state = status.RootElement.GetProperty("status_code").GetString();
            if (state == "FINISHED") { ready = true; break; }
            Rules.Require(state is not "ERROR" and not "EXPIRED", "Instagram could not process the image."); await Task.Delay(3000, ct);
        }
        Rules.Require(ready, "Instagram image processing timed out. Check the account before retrying.");
        var publish = Request(HttpMethod.Post, $"{Graph}/{a.ExternalId}/media_publish", a);
        publish.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["creation_id"] = containerId! });
        using var final = await Json(publish, ct); return final.RootElement.GetProperty("id").GetString()!;
    }
}
public class PublishingWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<PublishingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!config.GetValue<bool>("Publishing:Enabled")) continue;
            try
            {
                using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MarketingDb>();
                var due = await db.Posts.AsNoTracking().Where(x => x.Status == "Scheduled" && x.ScheduledAt <= DateTime.UtcNow).OrderBy(x => x.ScheduledAt).Take(10).ToListAsync(stoppingToken);
                var maxLateness = TimeSpan.FromMinutes(config.GetValue("Publishing:MaxLatenessMinutes", 60));
                foreach (var p in due)
                {
                    // After downtime or while publishing was disabled, don't release a backlog of stale posts at the wrong time.
                    if (p.ScheduledAt < DateTime.UtcNow - maxLateness)
                    {
                        var error = $"Not published: the scheduled time passed more than {maxLateness.TotalMinutes:0} minutes before the scheduler could run. Nothing was sent. Duplicate the post and schedule a new time.";
                        var expired = await db.Posts.Where(x => x.Id == p.Id && x.Status == "Scheduled" && x.Version == p.Version).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "NeedsAttention").SetProperty(x => x.Error, error).SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), stoppingToken);
                        if (expired == 1) { db.Audit.Add(new AuditEntry { BrandId = p.BrandId, PostId = p.Id, Action = "Missed schedule; not published" }); await db.SaveChangesAsync(stoppingToken); }
                        continue;
                    }
                    var claimed = await db.Posts.Where(x => x.Id == p.Id && x.Status == "Scheduled" && x.Version == p.Version).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Publishing").SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), stoppingToken);
                    if (claimed != 1) continue;
                    var post = await db.Posts.FindAsync([p.Id], stoppingToken);
                    try
                    {
                        var account = await db.Accounts.SingleOrDefaultAsync(x => x.BrandId == p.BrandId && x.Platform == p.Platform, stoppingToken) ?? throw new RuleException("Social account is disconnected.");
                        post!.RemoteId = await scope.ServiceProvider.GetRequiredService<Publisher>().Publish(post, account, stoppingToken);
                        post.Status = "Published"; post.PublishedAt = DateTime.UtcNow; post.Error = "";
                    }
                    catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                    {
                        post!.Status = "NeedsAttention";
                        post.Error = e is RuleException ? e.Message : "Delivery could not be confirmed. Check the social profile before creating a replacement draft.";
                        logger.LogWarning("Post {Id} needs attention ({Type})", p.Id, e.GetType().Name);
                    }
                    post!.UpdatedAt = DateTime.UtcNow; post.Version++;
                    db.Audit.Add(new AuditEntry { BrandId = post.BrandId, PostId = post.Id, Action = post.Status == "Published" ? "Published to " + post.Platform : "Delivery needs attention" });
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Publishing cycle failed ({Type})", e.GetType().Name); }
        }
    }
}
