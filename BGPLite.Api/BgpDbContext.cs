using BGPLite.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Api;

public class BgpDbContext : DbContext
{
    private readonly string _dbPath;

    public DbSet<Peer> Peers => Set<Peer>();
    public bool IsNewDatabase { get; }

    public BgpDbContext(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        IsNewDatabase = !File.Exists(dbPath);
        Database.EnsureCreated();

        Peers.Where(p => p.Status == "active").ExecuteUpdate(
            s => s.SetProperty(p => p.Status, "inactive"));
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={_dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Peer>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Ip).IsUnique();
            e.Property(p => p.Status).HasDefaultValue("inactive");
        });

        model.Entity<PeerCommunity>(e =>
        {
            e.HasKey(c => new { c.PeerId, c.Community });
            e.HasOne(c => c.Peer).WithMany(p => p.Communities)
                .HasForeignKey(c => c.PeerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PeerSubscription>(e =>
        {
            e.HasKey(s => new { s.PeerId, s.AsnListName });
            e.HasOne(s => s.Peer).WithMany(p => p.Subscriptions)
                .HasForeignKey(s => s.PeerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PeerCustomPrefix>(e =>
        {
            e.HasKey(c => new { c.PeerId, c.Prefix, c.PrefixLength });
            e.HasOne(c => c.Peer).WithMany(p => p.CustomPrefixes)
                .HasForeignKey(c => c.PeerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
