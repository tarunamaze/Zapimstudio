using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Zapim.Marketing;
public class RuleException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
public static class Rules
{
    public static void Require(bool condition, string message, int status = 400) { if (!condition) throw new RuleException(message, status); }
    public static int UserId(HttpContext ctx) => int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static void Admin(HttpContext ctx) => Require(ctx.User.IsInRole("Admin"), "Administrator access required.", 403);
    public static void Approver(HttpContext ctx) => Require(ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Approver"), "Approver access required.", 403);
    public static IQueryable<Brand> Brands(HttpContext ctx, MarketingDb db)
    {
        if (ctx.User.IsInRole("Admin")) return db.Brands;
        var userId = UserId(ctx); return db.Brands.Where(b => db.Memberships.Any(m => m.UserId == userId && m.BrandId == b.Id));
    }
    public static async Task<Brand> Brand(HttpContext ctx, MarketingDb db, int id) => await Brands(ctx, db).SingleOrDefaultAsync(x => x.Id == id) ?? throw new RuleException("Brand not found or access denied.", 404);
    public static async Task<Post> Post(HttpContext ctx, MarketingDb db, int id)
    {
        var p = await db.Posts.FindAsync(id) ?? throw new RuleException("Post not found.", 404); await Brand(ctx, db, p.BrandId); return p;
    }
    public static void Platform(string platform) => Require(platform is "LinkedIn" or "Instagram" or "Facebook", "Choose LinkedIn, Instagram or Facebook.");
    public static void ValidateLogin(LoginInput i)
    {
        Require(!string.IsNullOrWhiteSpace(i.Email) && i.Email.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(i.Email, out var addr) && addr.Address == i.Email.Trim(), "Enter a valid email.");
        Require(i.Password.Length is >= 12 and <= 256, "Use a password between 12 and 256 characters.");
    }
    public static void ApplyBrand(Brand b, BrandInput i)
    {
        Require(!string.IsNullOrWhiteSpace(i.Name) && i.Name.Length <= 80 && i.Description.Length <= 3000 && i.Tone.Length <= 1000 && i.Audience.Length <= 1000, "Brand name is required; keep description under 3,000 characters and audience and tone under 1,000.");
        Require(Regex.IsMatch(i.Color, "^#[0-9a-fA-F]{6}$"), "Choose a valid brand colour.");
        Require(i.Website == "" || (Uri.TryCreate(i.Website, UriKind.Absolute, out var u) && u.Scheme == "https"), "Use an HTTPS website address.");
        b.Name = i.Name.Trim(); b.Website = i.Website; b.Color = i.Color; b.Tone = i.Tone; b.Audience = i.Audience; b.Description = i.Description;
    }
    public static async Task ValidatePost(PostInput i, int brandId, MarketingDb db)
    {
        Platform(i.Platform); Require(!string.IsNullOrWhiteSpace(i.Title) && i.Title.Length <= 160, "Add a title under 160 characters.");
        var limit = i.Platform == "Instagram" ? 2200 : i.Platform == "LinkedIn" ? 3000 : 63206;
        Require(!string.IsNullOrWhiteSpace(i.Caption) && i.Caption.Length <= limit, $"Add a caption under {limit:N0} characters.");
        if (i.AssetId != "") Require(await db.Assets.AnyAsync(a => a.Id == i.AssetId && a.BrandId == brandId), "Choose an asset belonging to this brand.");
        Require(i.SuggestedAt is null || (i.SuggestedAt > DateTime.UtcNow.AddYears(-1) && i.SuggestedAt < DateTime.UtcNow.AddYears(2)), "Choose a suggested publish time within the next two years.");
    }
    public static void ApplyPost(Post p, PostInput i) { p.Title = i.Title.Trim(); p.Platform = i.Platform; p.Caption = i.Caption.Trim(); p.AssetId = i.AssetId; p.ScheduledAt = i.SuggestedAt?.ToUniversalTime(); }
    public static bool PublicUrl(IConfiguration c) => Uri.TryCreate(c["PublicBaseUrl"], UriKind.Absolute, out var uri) && uri.Scheme == "https" && !uri.IsLoopback;
}
public class Storage(string root)
{
    public string Root { get; } = root;
    public string AssetPath(string id) => Path.Combine(Root, "assets", id);
    public async Task<Asset> Save(MarketingDb db, int brandId, byte[] bytes, string name)
    {
        Rules.Require(bytes.Length is > 8 and <= 5_000_000, "Image must be under 5 MB.");
        var jpeg = bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
        var png = bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Rules.Require(jpeg || png, "Only JPEG and PNG images are accepted.");
        var asset = new Asset { BrandId = brandId, Name = name[..Math.Min(name.Length, 160)], ContentType = jpeg ? "image/jpeg" : "image/png" };
        await File.WriteAllBytesAsync(AssetPath(asset.Id), bytes); db.Assets.Add(asset); await db.SaveChangesAsync(); return asset;
    }
}
