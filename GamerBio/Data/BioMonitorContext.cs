using GamerBio.Models;
using Microsoft.EntityFrameworkCore;

namespace GamerBio.Data;

public class BioMonitorContext(DbContextOptions<BioMonitorContext> options) : DbContext(options)
{
    public DbSet<BioSignal> BioSignals => Set<BioSignal>();
    public DbSet<GalleryPhoto> GalleryPhotos => Set<GalleryPhoto>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BioSignal>(e =>
        {
            e.ToTable("biosignals");
            e.HasIndex(x => x.Timestamp);
        });

        modelBuilder.Entity<GalleryPhoto>(e =>
        {
            e.ToTable("gallery_photos");
            e.HasIndex(x => x.UploadedAt);
            e.Property(x => x.StoredName).HasMaxLength(64);
            e.Property(x => x.OriginalName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Caption).HasMaxLength(500);
        });
    }
}
