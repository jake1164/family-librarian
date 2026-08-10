using FamilyLibrarian.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    // Backs `PersistKeysToDbContext<AppDbContext>` (see DependencyInjection).
    // Without it the key ring lands in ~/.aspnet/DataProtection-Keys, which is
    // inside the container's own filesystem and dies with the container.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("identity");

        base.OnModelCreating(builder);

        builder.Entity<AppUser>(entity =>
        {
            entity.ToTable("users");
            entity.Property(user => user.DisplayName).HasMaxLength(256);
        });

        builder.Entity<IdentityRole<Guid>>().ToTable("roles");

        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");
    }
}
