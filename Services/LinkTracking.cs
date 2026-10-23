using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Zapim.Marketing;

public record LinkTrackingSaved(bool Enabled, string Medium, DateTime UpdatedAt);

// UTM tags on links to the brand's own website, added when a post is sent (the caption in the studio stays clean), so
// analytics can attribute visits to the channel and the post. Links to other sites are never changed.
public partial class LinkTracking(Storage storage, IDataProtectionProvider protection)
    : SavedSettings<LinkTrackingSaved>(storage, protection, "link-tracking.json", "link-tracking-v1")
{
    public bool Enabled => Load()?.Enabled ?? true;
    public string Medium => Load()?.Medium is { Length: > 0 } m ? m : "social";
    public DateTime? UpdatedAt => Load()?.UpdatedAt;

    public void Save(bool enabled, string medium)
    {
        medium = medium.Trim().ToLowerInvariant();
        Rules.Require(medium == "" || Token().IsMatch(medium), "Use letters, numbers, hyphens or underscores for the medium (e.g. social).");
        Write(new LinkTrackingSaved(enabled, medium, DateTime.UtcNow));
    }

    public static string Source(string platform) => platform switch { "Google" => "google_business", _ => platform.ToLowerInvariant() };
    public static string Campaign(string title)
    {
        var slug = NonWord().Replace(title.ToLowerInvariant().Normalize(NormalizationForm.FormKD), "-").Trim('-');
        return (slug.Length > 60 ? slug[..60].TrimEnd('-') : slug) is { Length: > 0 } s ? s : "post";
    }

    public string Apply(string caption, string website, string platform, string title) =>
        Enabled ? Tag(caption, website, Source(platform), Medium, Campaign(title)) : caption;

    public static string Tag(string caption, string website, string source, string medium, string campaign)
    {
        if (!Uri.TryCreate(website, UriKind.Absolute, out var site)) return caption;
        var host = site.Host.ToLowerInvariant().Replace("www.", "");
        return Url().Replace(caption, m =>
        {
            var raw = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"');
            var tail = m.Value[raw.Length..];
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return m.Value;
            var linkHost = uri.Host.ToLowerInvariant().Replace("www.", "");
            if ((linkHost != host && !linkHost.EndsWith("." + host)) || uri.Query.Contains("utm_", StringComparison.OrdinalIgnoreCase)) return m.Value;
            var fragment = raw.IndexOf('#') is var i and >= 0 ? raw[i..] : "";
            var bare = fragment.Length > 0 ? raw[..^fragment.Length] : raw;
            var query = $"utm_source={Uri.EscapeDataString(source)}&utm_medium={Uri.EscapeDataString(medium)}&utm_campaign={Uri.EscapeDataString(campaign)}";
            return bare + (bare.Contains('?') ? "&" : "?") + query + fragment + tail;
        });
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)] private static partial Regex Url();
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex NonWord();
    [GeneratedRegex(@"^[a-z0-9_-]{1,40}$")] private static partial Regex Token();
}
