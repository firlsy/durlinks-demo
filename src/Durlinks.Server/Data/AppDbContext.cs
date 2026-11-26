using Durlinks.Shared.Entities;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Link> Links => Set<Link>();
    public DbSet<LinkUrl> LinkUrls => Set<LinkUrl>();
    public DbSet<LinkSubLink> LinkSubLinks => Set<LinkSubLink>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<LinkTag> LinkTags => Set<LinkTag>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<PlatformOption> Platforms => Set<PlatformOption>();
    public DbSet<LinkTypeOption> LinkTypes => Set<LinkTypeOption>();
    public DbSet<User> Users => Set<User>();
    public DbSet<LlmConfig> LlmConfigs => Set<LlmConfig>();
    public DbSet<PublishLibrary> PublishLibraries => Set<PublishLibrary>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);

        m.Entity<Link>().HasIndex(x => x.IsDeleted);
        m.Entity<Link>().HasIndex(x => x.GroupName);
        m.Entity<Link>().HasIndex(x => x.LinkType);
        m.Entity<Link>().HasIndex(x => x.ImportSource);

        m.Entity<LinkUrl>()
            .HasOne(x => x.Link).WithMany(x => x.LinkUrls)
            .HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<LinkSubLink>()
            .HasOne(x => x.Link).WithMany(x => x.SubLinks)
            .HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<LinkTag>().HasKey(x => new { x.LinkId, x.TagId });
        m.Entity<LinkTag>()
            .HasOne(x => x.Link).WithMany(x => x.LinkTags)
            .HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<LinkTag>()
            .HasOne(x => x.Tag).WithMany(x => x.LinkTags)
            .HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<Tag>().HasIndex(x => x.Name).IsUnique();
        m.Entity<Group>().HasIndex(x => x.Name).IsUnique();
        m.Entity<PlatformOption>().HasIndex(x => x.Name).IsUnique();
        m.Entity<User>().HasIndex(x => x.Username).IsUnique();

        // 发布库
        m.Entity<PublishLibrary>().HasIndex(x => x.IsDeleted);
        m.Entity<PublishLibrary>().HasIndex(x => x.SortOrder);
        m.Entity<Link>().HasIndex(x => x.LibraryId);
        m.Entity<Link>()
            .HasOne(x => x.Library).WithMany(x => x.Links)
            .HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.SetNull);
    }
}
