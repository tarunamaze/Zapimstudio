using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Zapim.Marketing;

// Reads a public page on the brand's own website (for turning a blog post into social posts). HTTPS only, the brand's host
// or its subdomains only, public IP addresses only (checked when connecting, so redirects and DNS tricks cannot reach the
// server's network), at most 3 redirects on the same site, 2 MB and 20 seconds.
public static partial class PageFetcher
{
    public const string Client = "page-fetcher";

    public static void Register(IServiceCollection services) =>
        services.AddHttpClient(Client, c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("ZapimStudio/1.0 (+content repurposing)"); })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                    var address = addresses.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("The page's address is not a public internet address.");
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            });

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC);
        var b = ip.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168) || (b[0] == 192 && b[1] == 0 && b[2] == 0) || (b[0] == 198 && b[1] is 18 or 19) || b[0] >= 224);
    }

    public static Uri Check(string url, string website)
    {
        Rules.Require(Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort, "Enter an https:// address of a page on the brand's website.");
        Rules.Require(Uri.TryCreate(website, UriKind.Absolute, out var site), "Add the brand's website under Brands first.");
        var host = site!.Host.ToLowerInvariant().Replace("www.", ""); var pageHost = uri!.Host.ToLowerInvariant().Replace("www.", "");
        Rules.Require(pageHost == host || pageHost.EndsWith("." + host), $"Only pages on {host} can be used for this brand.");
        return uri;
    }

    public static async Task<(string Title, string Text)> Fetch(IHttpClientFactory factory, string url, string website)
    {
        var uri = Check(url, website);
        var client = factory.CreateClient(Client);
        for (var hop = 0; hop <= 3; hop++)
        {
            using var response = await Send(client, uri);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                uri = Check(new Uri(uri, next).AbsoluteUri, website); continue;
            }
            Rules.Require(response.IsSuccessStatusCode, $"The page returned HTTP {(int)response.StatusCode}.");
            Rules.Require(response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml" or null, "That address is not a web page.");
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[2_000_000]; var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read)) is var n and > 0) read += n;
            return Extract(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        throw new RuleException("The page redirected too many times.");
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, Uri uri)
    {
        try { return await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new RuleException("The page could not be read. Check the address and that the site is up.", 502); }
    }

    // Visible text only: title, then headings and paragraphs without scripts, styles, navigation or footers.
    public static (string Title, string Text) Extract(string html)
    {
        var title = WebUtility.HtmlDecode(TitleTag().Match(html).Groups[1].Value).Trim();
        var body = Noise().Replace(html, " ");
        body = Breaks().Replace(body, "\n");
        body = WebUtility.HtmlDecode(Tags().Replace(body, " "));
        body = Spaces().Replace(body, " ");
        body = string.Join("\n", body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 1));
        Rules.Require(body.Length > 200, "That page has too little text to work from.");
        return (title.Length > 200 ? title[..200] : title, body.Length > 12000 ? body[..12000] : body);
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TitleTag();
    [GeneratedRegex(@"<(script|style|noscript|svg|nav|footer|header|form|iframe)\b.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex Noise();
    [GeneratedRegex(@"<\s*(br|/p|/h[1-6]|/li|/div|/tr|/section|/article)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Breaks();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"[ \t\r\f\v]+")] private static partial Regex Spaces();
}
