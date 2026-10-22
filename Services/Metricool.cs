using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Zapim.Marketing;

public record MetricoolBrand(string BlogId, string Label, string[] Networks);

// One Metricool account (server-wide token and user ID) for all brands; each Zapim brand maps to one Metricool brand (blogId).
// The mapping is stored as the brand's SocialAccount with Platform "Metricool" and ExternalId = blogId.
public class MetricoolClient(IHttpClientFactory factory, IConfiguration config)
{
    public const string Platform = "Metricool";
    private string Api => (config["Metricool:BaseUrl"] is { Length: > 0 } url ? url : "https://app.metricool.com/api").TrimEnd('/');
    public static bool Configured(IConfiguration c) => !string.IsNullOrWhiteSpace(c["Metricool:UserToken"]) && !string.IsNullOrWhiteSpace(c["Metricool:UserId"]);
    private static readonly Dictionary<string, string> Networks = new() { ["LinkedIn"] = "linkedin", ["Facebook"] = "facebook", ["Instagram"] = "instagram" };

    private HttpRequestMessage Request(HttpMethod method, string path, string blogId)
    {
        Rules.Require(Configured(config), "Metricool is not configured on the server.");
        var r = new HttpRequestMessage(method, $"{Api}{path}?blogId={Uri.EscapeDataString(blogId)}&userId={Uri.EscapeDataString(config["Metricool:UserId"]!)}");
        r.Headers.Add("X-Mc-Auth", config["Metricool:UserToken"]);
        return r;
    }
    private async Task<JsonDocument> Json(HttpRequestMessage r, CancellationToken ct)
    {
        using (r)
        using (var response = await factory.CreateClient("external").SendAsync(r, ct))
        {
            Rules.Require((int)response.StatusCode is not (401 or 403), "Metricool rejected the API token. Check Metricool:UserToken and Metricool:UserId, and that the plan includes API access.", 502);
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
