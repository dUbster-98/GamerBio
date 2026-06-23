using GamerBio.Models;
using Microsoft.EntityFrameworkCore;

namespace GamerBio.Data;

public class BioMonitorContext(DbContextOptions<BioMonitorContext> options) : DbContext(options)
{
    public DbSet<BioSignal> BioSignals => Set<BioSignal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BioSignal>(e =>
        {
            e.ToTable("biosignals");
            e.HasIndex(x => x.Timestamp);
        });
    }
}
