using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Domain.Catalog;
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

    public DbSet<Author> Authors => Set<Author>();

    public DbSet<Work> Works => Set<Work>();

    public DbSet<Edition> Editions => Set<Edition>();

    public DbSet<Series> Series => Set<Series>();

    public DbSet<SeriesEntry> SeriesEntries => Set<SeriesEntry>();

    public DbSet<ExternalReference> ExternalReferences => Set<ExternalReference>();

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

        ConfigureCatalog(builder);
    }

    private static void ConfigureCatalog(ModelBuilder builder)
    {
        builder.Entity<Author>(entity =>
        {
            entity.ToTable("authors", "catalog");
            entity.HasKey(author => author.Id);
            entity.Property(author => author.Id).HasColumnName("id");
            entity.Property(author => author.CanonicalName).HasColumnName("canonical_name").HasMaxLength(512).IsRequired();
            entity.Property(author => author.NormalizedName).HasColumnName("normalized_name").HasMaxLength(512).IsRequired();
            entity.Property(author => author.SortName).HasColumnName("sort_name").HasMaxLength(512);
            entity.Property(author => author.Biography).HasColumnName("biography").HasMaxLength(16_000);
            ConfigureCatalogTimestamps(entity);
            entity.HasIndex(author => author.NormalizedName);
        });

        builder.Entity<Work>(entity =>
        {
            entity.ToTable("works", "catalog");
            entity.HasKey(work => work.Id);
            entity.Property(work => work.Id).HasColumnName("id");
            entity.Property(work => work.CanonicalTitle).HasColumnName("canonical_title").HasMaxLength(1_000).IsRequired();
            entity.Property(work => work.NormalizedTitle).HasColumnName("normalized_title").HasMaxLength(1_000).IsRequired();
            entity.Property(work => work.Description).HasColumnName("description").HasMaxLength(16_000);
            entity.Property(work => work.CoverUrl).HasColumnName("cover_url").HasMaxLength(2_048);
            entity.Property(work => work.FirstPublicationDate).HasColumnName("first_publication_date");
            entity.Property(work => work.PublicationStatus).HasColumnName("publication_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(work => work.IsRetired).HasColumnName("is_retired");
            entity.Property(work => work.ReplacedById).HasColumnName("replaced_by_id");
            ConfigureCatalogTimestamps(entity);
            entity.HasIndex(work => work.NormalizedTitle);
            entity.HasIndex(work => work.ReplacedById);
            entity.HasOne<Work>()
                .WithMany()
                .HasForeignKey(work => work.ReplacedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WorkAuthor>(entity =>
        {
            entity.ToTable("work_authors", "catalog");
            entity.HasKey(workAuthor => new { workAuthor.WorkId, workAuthor.AuthorId });
            entity.Property(workAuthor => workAuthor.WorkId).HasColumnName("work_id");
            entity.Property(workAuthor => workAuthor.AuthorId).HasColumnName("author_id");
            entity.Property(workAuthor => workAuthor.Ordinal).HasColumnName("ordinal");
            entity.Property(workAuthor => workAuthor.Role).HasColumnName("role").HasMaxLength(128);
            entity.HasIndex(workAuthor => new { workAuthor.WorkId, workAuthor.Ordinal }).IsUnique();
            entity.HasOne(workAuthor => workAuthor.Work)
                .WithMany(work => work.Authors)
                .HasForeignKey(workAuthor => workAuthor.WorkId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(workAuthor => workAuthor.Author)
                .WithMany(author => author.WorkAuthors)
                .HasForeignKey(workAuthor => workAuthor.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Edition>(entity =>
        {
            entity.ToTable("editions", "catalog");
            entity.HasKey(edition => edition.Id);
            entity.Property(edition => edition.Id).HasColumnName("id");
            entity.Property(edition => edition.WorkId).HasColumnName("work_id");
            entity.Property(edition => edition.Title).HasColumnName("title").HasMaxLength(1_000).IsRequired();
            entity.Property(edition => edition.Publisher).HasColumnName("publisher").HasMaxLength(512);
            entity.Property(edition => edition.Language).HasColumnName("language").HasMaxLength(32);
            entity.Property(edition => edition.PublicationDate).HasColumnName("publication_date");
            entity.Property(edition => edition.Isbn13).HasColumnName("isbn13").HasMaxLength(13);
            entity.Property(edition => edition.Format).HasColumnName("format").HasConversion<string>().HasMaxLength(32);
            ConfigureCatalogTimestamps(entity);
            entity.HasIndex(edition => edition.Isbn13).IsUnique().HasFilter("isbn13 IS NOT NULL");
            entity.HasOne(edition => edition.Work)
                .WithMany(work => work.Editions)
                .HasForeignKey(edition => edition.WorkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Series>(entity =>
        {
            entity.ToTable("series", "catalog");
            entity.HasKey(series => series.Id);
            entity.Property(series => series.Id).HasColumnName("id");
            entity.Property(series => series.Name).HasColumnName("name").HasMaxLength(1_000).IsRequired();
            entity.Property(series => series.NormalizedName).HasColumnName("normalized_name").HasMaxLength(1_000).IsRequired();
            entity.Property(series => series.Description).HasColumnName("description").HasMaxLength(16_000);
            entity.Property(series => series.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            ConfigureCatalogTimestamps(entity);
            entity.HasIndex(series => series.NormalizedName);
        });

        builder.Entity<SeriesEntry>(entity =>
        {
            entity.ToTable("series_entries", "catalog");
            entity.HasKey(seriesEntry => seriesEntry.Id);
            entity.Property(seriesEntry => seriesEntry.Id).HasColumnName("id");
            entity.Property(seriesEntry => seriesEntry.SeriesId).HasColumnName("series_id");
            entity.Property(seriesEntry => seriesEntry.WorkId).HasColumnName("work_id");
            entity.Property(seriesEntry => seriesEntry.PositionLabel).HasColumnName("position_label").HasMaxLength(128);
            entity.Property(seriesEntry => seriesEntry.PositionSort).HasColumnName("position_sort").HasPrecision(10, 3);
            entity.Property(seriesEntry => seriesEntry.IsPrimary).HasColumnName("is_primary");
            ConfigureCatalogTimestamps(entity);
            entity.HasIndex(seriesEntry => new { seriesEntry.SeriesId, seriesEntry.WorkId }).IsUnique();
            entity.HasIndex(seriesEntry => new
            {
                seriesEntry.SeriesId,
                seriesEntry.PositionSort,
                seriesEntry.PositionLabel
            });
            entity.HasOne(seriesEntry => seriesEntry.Series)
                .WithMany(series => series.Entries)
                .HasForeignKey(seriesEntry => seriesEntry.SeriesId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(seriesEntry => seriesEntry.Work)
                .WithMany(work => work.SeriesEntries)
                .HasForeignKey(seriesEntry => seriesEntry.WorkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ExternalReference>(entity =>
        {
            entity.ToTable("external_references", "catalog");
            entity.HasKey(reference => reference.Id);
            entity.Property(reference => reference.Id).HasColumnName("id");
            entity.Property(reference => reference.ProviderId).HasColumnName("provider_id").HasMaxLength(128).IsRequired();
            entity.Property(reference => reference.EntityType).HasColumnName("entity_type").HasConversion<string>().HasMaxLength(32);
            entity.Property(reference => reference.EntityId).HasColumnName("entity_id");
            entity.Property(reference => reference.ExternalId).HasColumnName("external_id").HasMaxLength(512).IsRequired();
            entity.Property(reference => reference.SourceUrl).HasColumnName("source_url").HasMaxLength(2_048);
            entity.Property(reference => reference.ObservedAtUtc).HasColumnName("observed_at_utc");
            ConfigureCatalogTimestamps(entity);
            entity.HasIndex(reference => new
            {
                reference.ProviderId,
                reference.EntityType,
                reference.ExternalId
            }).IsUnique();
            entity.HasIndex(reference => new { reference.EntityType, reference.EntityId });
        });
    }

    private static void ConfigureCatalogTimestamps<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class
    {
        entity.Property("CreatedAtUtc").HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        entity.Property("UpdatedAtUtc").HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
        entity.Property("Version").HasColumnName("xmin").IsRowVersion();
    }
}
