using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Zapim.Marketing;

public class Brand
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Website { get; set; } = "";
    public string Color { get; set; } = "#087f76";
    public string Tone { get; set; } = "Clear, confident and helpful";
    public string Audience { get; set; } = "";
    public string Description { get; set; } = "";
    public string LogoAssetId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class TeamUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    [JsonIgnore] public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Editor";
    public bool Active { get; set; } = true;
    public List<Membership> Memberships { get; set; } = [];
}
public class Membership
{
    public int UserId { get; set; }
    public int BrandId { get; set; }
}
public class Post
{
    public int Id { get; set; }
    public int BrandId { get; set; }
    public string Title { get; set; } = "";
    public string Platform { get; set; } = "LinkedIn";
    public string Caption { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public DateTime? ScheduledAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;
    public int CreatedBy { get; set; }
    public int? ApprovedBy { get; set; }
    public string ReviewNote { get; set; } = "";
    public string RemoteId { get; set; } = "";
    public string Error { get; set; } = "";
}
public class SocialAccount
{
    public int Id { get; set; }
    public int BrandId { get; set; }
    public string Platform { get; set; } = "";
    public string Name { get; set; } = "";
    public string ExternalId { get; set; } = "";
    [JsonIgnore] public string ProtectedToken { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
}
public class Asset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int BrandId { get; set; }
    public string ContentType { get; set; } = "image/jpeg";
    public string Name { get; set; } = "creative.jpg";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class AuditEntry
{
    public int Id { get; set; }
    public int BrandId { get; set; }
    public int? PostId { get; set; }
    public int? UserId { get; set; }
    public string Action { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}
public class MarketingDb(DbContextOptions<MarketingDb> options) : DbContext(options)
{
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<TeamUser> Users => Set<TeamUser>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<SocialAccount> Accounts => Set<SocialAccount>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TeamUser>().HasIndex(x => x.Email).IsUnique();
        b.Entity<Membership>().HasKey(x => new { x.UserId, x.BrandId });
        b.Entity<TeamUser>().HasMany(x => x.Memberships).WithOne().HasForeignKey(x => x.UserId);
        b.Entity<Membership>().HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId);
        b.Entity<Post>().HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId);
        b.Entity<Post>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Post>().HasIndex(x => new { x.Status, x.ScheduledAt });
        b.Entity<SocialAccount>().HasIndex(x => new { x.BrandId, x.Platform }).IsUnique();
    }
}
