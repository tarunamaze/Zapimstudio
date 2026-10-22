using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Zapim.Marketing;

public record MetricoolBrand(string BlogId, string Label, string[] Networks);
public record MetricoolCredentials(string UserId, string Token);

// The one Metricool account used by all brands. Saved from the Settings page into the storage directory, with the token
// encrypted like social tokens; otherwise read from server configuration (Metricool:UserId, Metricool:UserToken).
public class MetricoolSettings(IConfiguration config, Storage storage, IDataProtectionProvider protection)
{
    private record Saved(string UserId, string ProtectedToken, DateTime UpdatedAt);
    private readonly Lock gate = new();
    private Saved? saved;
    private bool loaded;
    private string FilePath => Path.Combine(storage.Root, "metricool.json");
    private IDataProtector Protector => protection.CreateProtector("metricool-token-v1");

    private Saved? Load()
    {
        lock (gate)
        {
            if (!loaded) { saved = File.Exists(FilePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) : null; loaded = true; }
            return saved;
        }
    }
    public string Source => Load() is not null ? "app" : FromConfig() is not null ? "server" : "";
    public DateTime? UpdatedAt => Load()?.UpdatedAt;
    private MetricoolCredentials? FromConfig() =>
        config["Metricool:UserId"] is { Length: > 0 } id && config["Metricool:UserToken"] is { Length: > 0 } token ? new(id, token) : null;
    public MetricoolCredentials? Current => Load() is { } s ? new(s.UserId, Protector.Unprotect(s.ProtectedToken)) : FromConfig();
    public bool Configured => Current is not null;

    // A blank token keeps the token already in use.
    public void Save(string userId, string token)
    {
        Rules.Require(System.Text.RegularExpressions.Regex.IsMatch(userId, "^[0-9]{1,18}$"), "Enter the numeric Metricool user ID (the userId in the Metricool address bar).");
        token = token.Trim();
        if (token == "") token = Current?.Token ?? "";
        Rules.Require(token.Length is >= 10 and <= 4000, "Enter the Metricool API token from Metricool, Account settings, API.");
        lock (gate)
        {
            saved = new Saved(userId, Protector.Protect(token), DateTime.UtcNow); loaded = true;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(saved));
        }
    }
    public void Clear() { lock (gate) { File.Delete(FilePath); saved = null; loaded = true; } }
}

// Each Zapim brand maps to one Metricool brand (blogId), stored as the brand's SocialAccount with Platform "Metricool".
public class MetricoolClient(IHttpClientFactory factory, IConfiguration config, MetricoolSettings settings)
{
    public const string Platform = "Metricool";
    private string Api => (config["Metricool:BaseUrl"] is { Length: > 0 } url ? url : "https://app.metricool.com/api").TrimEnd('/');
    private static readonly Dictionary<string, string> Networks = new() { ["LinkedIn"] = "linkedin", ["Facebook"] = "facebook", ["Instagram"] = "instagram" };

    private HttpRequestMessage Request(HttpMethod method, string path, string blogId)
    {
        var account = settings.Current ?? throw new RuleException("Metricool is not set up. An administrator can add it under Settings.");
        var r = new HttpRequestMessage(method, $"{Api}{path}?blogId={Uri.EscapeDataString(blogId)}&userId={Uri.EscapeDataString(account.UserId)}");
        r.Headers.Add("X-Mc-Auth", account.Token);
        return r;
    }
    private async Task<JsonDocument> Json(HttpRequestMessage r, CancellationToken ct)
    {
        using (r)
        using (var response = await factory.CreateClient("external").SendAsync(r, ct))
        {
            Rules.Require((int)response.StatusCode is not (401 or 403), "Metricool rejected the API token. Check the user ID and API token under Settings, and that the Metricool plan includes API access.", 502);
            Rules.Require(response.IsSuccessStatusCode, $"Metricool returned HTTP {(int)response.StatusCode}. Check the brand in Metricool before retrying.", 502);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }

    // Lists the account's brands (authenticated with the requested blogId) and returns that brand with its connected networks.
    public async Task<MetricoolBrand> Brand(string blogId, CancellationToken ct = default)
    {
        using var profiles = await Json(Request(HttpMethod.Get, "/admin/simpleProfiles", blogId), ct);
        var brands = profiles.RootElement.ValueKind == JsonValueKind.Array ? profiles.RootElement : profiles.RootElement.GetProperty("data");
        foreach (var b in brands.EnumerateArray())
        {
            if (!b.TryGetProperty("id", out var id) || id.ToString() != blogId) continue;
            string Text(string name) => b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var networks = new List<string>();
            if (Text("linkedinCompany") != "") networks.Add("LinkedIn");
            if (Text("facebookPageId") != "" || Text("facebook") != "") networks.Add("Facebook");
            if (Text("instagram") != "") networks.Add("Instagram");
            var label = new[] { Text("label"), Text("title"), "Metricool brand " + blogId }.First(x => x != "");
            return new MetricoolBrand(blogId, label[..Math.Min(label.Length, 100)], [.. networks]);
        }
        throw new RuleException("This Metricool account has no brand with that ID.");
    }

    // Hands the post to Metricool's scheduler for immediate publication and returns Metricool's post ID.
    public async Task<string> Schedule(Post post, string blogId, string? mediaUrl, CancellationToken ct)
    {
        var network = Networks[post.Platform];
        // Metricool only accepts future publication times.
        var at = DateTime.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var body = new Dictionary<string, object>
        {
            ["publicationDate"] = new { dateTime = at, timezone = "UTC" },
            ["text"] = post.Caption,
            ["providers"] = new[] { new { network } },
            ["media"] = mediaUrl is null ? Array.Empty<string>() : [mediaUrl],
            ["saveExternalMediaFiles"] = mediaUrl is not null,
            ["autoPublish"] = true,
            ["draft"] = false,
            ["shortener"] = false,
        };
        if (post.Platform == "Facebook") body["facebookData"] = new { type = "POST" };
        if (post.Platform == "Instagram") body["instagramData"] = new { type = "POST", autoPublish = true };
        var request = Request(HttpMethod.Post, "/v2/scheduler/posts", blogId);
        request.Content = JsonContent.Create(body);
        using var result = await Json(request, ct);
        var data = result.RootElement.TryGetProperty("data", out var d) ? d : result.RootElement;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("id", out var postId)) throw new RuleException("Metricool returned no post ID. Check the Metricool planner before retrying.");
        return postId.ToString();
    }
}
