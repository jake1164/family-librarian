using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Integrations;
using FamilyLibrarian.Domain.Requests;
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

    public DbSet<MetadataProviderSetting> MetadataProviderSettings => Set<MetadataProviderSetting>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<BookRequest> BookRequests => Set<BookRequest>();

    public DbSet<RequestFormat> RequestFormats => Set<RequestFormat>();

    public DbSet<RequestStatusHistory> RequestStatusHistory => Set<RequestStatusHistory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("identity");

        base.OnModelCreating(builder);

        builder.Entity<AppUser>(entity =>
        {
            entity.ToTable("users");
            entity.Property(user => user.DisplayName).HasMaxLength(256);
            // No HasDefaultValue: the column default existed only to backfill
            // rows that predated this column. Keeping it in the model makes EF
            // warn that it cannot tell a deliberate value from the CLR default,
            // and the application always sets Status explicitly anyway.
            entity.Property(user => user.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(32);
            entity.HasIndex(user => user.Status);
        });

        ConfigureInvitations(builder);

        builder.Entity<IdentityRole<Guid>>().ToTable("roles");

        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        ConfigureCatalog(builder);
        ConfigureRequests(builder);
        ConfigureIntegrations(builder);
        ConfigureAudit(builder);
    }

    private static void ConfigureInvitations(ModelBuilder builder)
    {
        builder.Entity<Invitation>(entity =>
        {
            entity.ToTable("invitations", "identity");
            entity.HasKey(invitation => invitation.Id);
            entity.Property(invitation => invitation.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(invitation => invitation.Email)
                .HasColumnName("email").HasMaxLength(Invitation.MaxEmailLength).IsRequired();
            entity.Property(invitation => invitation.NormalizedEmail)
                .HasColumnName("normalized_email").HasMaxLength(Invitation.MaxEmailLength).IsRequired();
            // Unique so one token can never resolve to two invitations, and
            // indexed because redemption looks up by exactly this value.
            entity.Property(invitation => invitation.TokenHash)
                .HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            entity.HasIndex(invitation => invitation.TokenHash).IsUnique();
            entity.Property(invitation => invitation.Role)
                .HasColumnName("role").HasMaxLength(64).IsRequired();
            entity.Property(invitation => invitation.InvitedByUserId).HasColumnName("invited_by_user_id");
            entity.Property(invitation => invitation.CreatedAtUtc)
                .HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(invitation => invitation.ExpiresAtUtc)
                .HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(invitation => invitation.RedeemedAtUtc)
                .HasColumnName("redeemed_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(invitation => invitation.RedeemedByUserId).HasColumnName("redeemed_by_user_id");
            entity.Property(invitation => invitation.RevokedAtUtc)
                .HasColumnName("revoked_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(invitation => invitation.RevokedByUserId).HasColumnName("revoked_by_user_id");
            // Two simultaneous redemptions of one token both pass the "can be
            // redeemed" check; the row version is what makes the second fail.
            entity.Property(invitation => invitation.Version).HasColumnName("xmin").IsRowVersion();
            entity.HasIndex(invitation => invitation.NormalizedEmail);

            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(invitation => invitation.InvitedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureRequests(ModelBuilder builder)
    {
        builder.Entity<BookRequest>(entity =>
        {
            entity.ToTable("book_requests", "requests");
            entity.HasKey(request => request.Id);
            // ValueGeneratedNever on every key in this aggregate. The application
            // assigns the GUIDs, and without this EF sees a child that already
            // carries a key, assumes it came from the database, and issues an
            // UPDATE for a row that does not exist yet.
            entity.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(request => request.UserId).HasColumnName("user_id");
            entity.Property(request => request.WorkId).HasColumnName("work_id");
            entity.Property(request => request.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(request => request.RequesterNote).HasColumnName("requester_note").HasMaxLength(BookRequest.MaxNoteLength);
            entity.Property(request => request.AdminNote).HasColumnName("admin_note").HasMaxLength(BookRequest.MaxAdminNoteLength);
            entity.Property(request => request.RequestedAtUtc).HasColumnName("requested_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(request => request.StatusChangedAtUtc).HasColumnName("status_changed_at_utc").HasColumnType("timestamp with time zone");
            ConfigureTimestamps(entity);

            // My Requests filters by owner; the admin queue filters by status and
            // orders by recency.
            entity.HasIndex(request => new { request.UserId, request.WorkId, request.Status });
            entity.HasIndex(request => new { request.Status, request.UpdatedAtUtc });

            // Restrict, not Cascade: a completed request and its history outlive
            // catalog corrections and user deactivation.
            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(request => request.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Work>()
                .WithMany()
                .HasForeignKey(request => request.WorkId)
                .OnDelete(DeleteBehavior.Restrict);

            // Both collections are exposed read-only and written only through
            // BookRequest's own methods, so EF reads and writes the backing field
            // rather than the property.
            entity.HasMany(request => request.Formats)
                .WithOne(format => format.Request)
                .HasForeignKey(format => format.RequestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(request => request.StatusHistory)
                .WithOne(history => history.Request)
                .HasForeignKey(history => history.RequestId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Navigation(request => request.Formats).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.Navigation(request => request.StatusHistory).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<RequestFormat>(entity =>
        {
            entity.ToTable("request_formats", "requests");
            entity.HasKey(format => format.Id);
            entity.Property(format => format.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(format => format.RequestId).HasColumnName("request_id");
            entity.Property(format => format.MediaType).HasColumnName("media_type").HasConversion<string>().HasMaxLength(32);
            entity.Property(format => format.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            ConfigureTimestamps(entity);

            // One row per media type per request: the database, not the command
            // handler, is what makes "ebook twice" impossible.
            entity.HasIndex(format => new { format.RequestId, format.MediaType }).IsUnique();
        });

        builder.Entity<RequestStatusHistory>(entity =>
        {
            entity.ToTable("request_status_history", "requests");
            entity.HasKey(history => history.Id);
            entity.Property(history => history.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(history => history.RequestId).HasColumnName("request_id");
            entity.Property(history => history.FromStatus).HasColumnName("from_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(history => history.ToStatus).HasColumnName("to_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(history => history.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(history => history.Reason).HasColumnName("reason").HasMaxLength(BookRequest.MaxReasonLength);
            entity.Property(history => history.OccurredAtUtc).HasColumnName("occurred_at_utc").HasColumnType("timestamp with time zone");

            entity.HasIndex(history => new { history.RequestId, history.OccurredAtUtc });
        });
    }

    private static void ConfigureIntegrations(ModelBuilder builder)
    {
        builder.Entity<MetadataProviderSetting>(entity =>
        {
            entity.ToTable("metadata_provider_settings", "app");
            // The provider id is the natural key: routes address known installed
            // provider ids, so a surrogate key would add a lookup without adding
            // any meaning.
            entity.HasKey(setting => setting.ProviderId);
            entity.Property(setting => setting.ProviderId).HasColumnName("provider_id").HasMaxLength(128);
            entity.Property(setting => setting.IsEnabled).HasColumnName("is_enabled");
            entity.Property(setting => setting.ProtectedCredential).HasColumnName("protected_credential").HasMaxLength(8_192);
            entity.Property(setting => setting.CredentialFormatVersion).HasColumnName("credential_format_version");
            entity.Property(setting => setting.CredentialSetAtUtc).HasColumnName("credential_set_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(setting => setting.CredentialHint).HasColumnName("credential_hint").HasMaxLength(8);
            entity.Property(setting => setting.LastTestedAtUtc).HasColumnName("last_tested_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(setting => setting.LastTestSucceeded).HasColumnName("last_test_succeeded");
            entity.Property(setting => setting.LastTestMessage).HasColumnName("last_test_message").HasMaxLength(512);
            entity.Property(setting => setting.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(setting => setting.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(setting => setting.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(setting => setting.Version).HasColumnName("xmin").IsRowVersion();
        });
    }

    private static void ConfigureAudit(ModelBuilder builder)
    {
        builder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events", "audit");
            entity.HasKey(auditEvent => auditEvent.Id);
            entity.Property(auditEvent => auditEvent.Id).HasColumnName("id");
            entity.Property(auditEvent => auditEvent.Action).HasColumnName("action").HasMaxLength(128).IsRequired();
            entity.Property(auditEvent => auditEvent.SubjectType).HasColumnName("subject_type").HasMaxLength(128).IsRequired();
            entity.Property(auditEvent => auditEvent.SubjectId).HasColumnName("subject_id").HasMaxLength(256);
            entity.Property(auditEvent => auditEvent.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(auditEvent => auditEvent.ActorDisplayName).HasColumnName("actor_display_name").HasMaxLength(256);
            entity.Property(auditEvent => auditEvent.Detail).HasColumnName("detail").HasColumnType("jsonb");
            entity.Property(auditEvent => auditEvent.OccurredAtUtc).HasColumnName("occurred_at_utc").HasColumnType("timestamp with time zone");
            entity.HasIndex(auditEvent => auditEvent.OccurredAtUtc);
            entity.HasIndex(auditEvent => new { auditEvent.SubjectType, auditEvent.SubjectId });
        });
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
            ConfigureTimestamps(entity);
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
            ConfigureTimestamps(entity);
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
            ConfigureTimestamps(entity);
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
            ConfigureTimestamps(entity);
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
            ConfigureTimestamps(entity);
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
            ConfigureTimestamps(entity);
            entity.HasIndex(reference => new
            {
                reference.ProviderId,
                reference.EntityType,
                reference.ExternalId
            }).IsUnique();
            entity.HasIndex(reference => new { reference.EntityType, reference.EntityId });
        });
    }

    private static void ConfigureTimestamps<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class
    {
        entity.Property("CreatedAtUtc").HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        entity.Property("UpdatedAtUtc").HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
        entity.Property("Version").HasColumnName("xmin").IsRowVersion();
    }
}
