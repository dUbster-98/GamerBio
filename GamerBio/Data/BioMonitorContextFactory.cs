using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GamerBio.Data;

public class BioMonitorContextFactory : IDesignTimeDbContextFactory<BioMonitorContext>
{
    public BioMonitorContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BioMonitorContext>()
            .UseNpgsql("Host=localhost;Database=biomonitor;Username=biomonitor;Password=designtime")
            .Options;
        return new BioMonitorContext(options);
    }
}
