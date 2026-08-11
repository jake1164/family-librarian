using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FamilyLibrarian.Infrastructure.Persistence;

/// <summary>
/// Supplies EF tooling with the model without starting the web host.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=family_librarian_design;Username=design;Password=design",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new AppDbContext(options);
    }
}
