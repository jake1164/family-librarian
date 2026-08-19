using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Feedback;
using FamilyLibrarian.Domain.Policy;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
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

    public DbSet<ProviderSetting> ProviderSettings => Set<ProviderSetting>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<BookRequest> BookRequests => Set<BookRequest>();

    public DbSet<RequestFormat> RequestFormats => Set<RequestFormat>();

    public DbSet<RequestStatusHistory> RequestStatusHistory => Set<RequestStatusHistory>();

    public DbSet<UserWorkFeedback> UserWorkFeedback => Set<UserWorkFeedback>();

    public DbSet<AcquisitionJob> AcquisitionJobs => Set<AcquisitionJob>();

    public DbSet<AcquisitionCandidate> AcquisitionCandidates => Set<AcquisitionCandidate>();

    public DbSet<ProviderAttempt> ProviderAttempts => Set<ProviderAttempt>();

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    public DbSet<SecurityEvaluation> SecurityEvaluations => Set<SecurityEvaluation>();

    public DbSet<CwaSettings> CwaSettings => Set<CwaSettings>();

    public DbSet<AudiobookshelfSettings> AudiobookshelfSettings => Set<AudiobookshelfSettings>();

    public DbSet<LibraryImport> LibraryImports => Set<LibraryImport>();

    public DbSet<Delivery> Deliveries => Set<Delivery>();

    public DbSet<AcquisitionPolicySettings> AcquisitionPolicySettings => Set<AcquisitionPolicySettings>();

    public DbSet<OidcSettings> OidcSettings => Set<OidcSettings>();

    public DbSet<ExternalProvider> ExternalProviders => Set<ExternalProvider>();

    public DbSet<PrivateEgressGatewaySettings> PrivateEgressGatewaySettings => Set<PrivateEgressGatewaySettings>();

    public DbSet<ProviderCatalog> ProviderCatalogs => Set<ProviderCatalog>();

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
        ConfigureFeedback(builder);
        ConfigureProviders(builder);
        ConfigureAcquisition(builder);
        ConfigureSecurity(builder);
        ConfigureAudit(builder);
        ConfigurePublishing(builder);
        ConfigurePolicy(builder);
        ConfigureAuthentication(builder);
    }

    private static void ConfigureAuthentication(ModelBuilder builder)
    {
        builder.Entity<OidcSettings>(entity =>
        {
            entity.ToTable("oidc_settings", "identity");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(settings => settings.IsEnabled).HasColumnName("is_enabled");
            entity.Property(settings => settings.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
            entity.Property(settings => settings.Authority).HasColumnName("authority").HasMaxLength(512);
            entity.Property(settings => settings.ClientId).HasColumnName("client_id").HasMaxLength(256);
            entity.Property(settings => settings.ProtectedClientSecret).HasColumnName("protected_client_secret").HasMaxLength(2_048);
            entity.Property(settings => settings.ClientSecretFormatVersion).HasColumnName("client_secret_format_version");
            entity.Property(settings => settings.ClientSecretHint).HasColumnName("client_secret_hint").HasMaxLength(8);
            entity.Property(settings => settings.ClientSecretSetAtUtc).HasColumnName("client_secret_set_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.Scopes).HasColumnName("scopes").HasMaxLength(512).IsRequired();
            entity.Property(settings => settings.MatchClaimName).HasColumnName("match_claim_name").HasMaxLength(128).IsRequired();
            entity.Property(settings => settings.AdminClaimName).HasColumnName("admin_claim_name").HasMaxLength(128);
            entity.Property(settings => settings.AdminClaimValues).HasColumnName("admin_claim_values").HasMaxLength(1_024);
            entity.Property(settings => settings.AutoCreateAccounts).HasColumnName("auto_create_accounts");
            entity.Property(settings => settings.LocalLoginDisabled).HasColumnName("local_login_disabled");
            entity.Property(settings => settings.LastTestedAtUtc).HasColumnName("last_tested_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.LastTestSucceeded).HasColumnName("last_test_succeeded");
            entity.Property(settings => settings.LastTestMessage).HasColumnName("last_test_message").HasMaxLength(512);
            entity.Property(settings => settings.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(settings => settings.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.Version).HasColumnName("xmin").IsRowVersion();
        });
    }

    private static void ConfigureFeedback(ModelBuilder builder)
    {
        builder.Entity<UserWorkFeedback>(entity =>
        {
            entity.ToTable("user_work_feedback", "feedback");
            entity.HasKey(feedback => feedback.Id);
            entity.Property(feedback => feedback.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(feedback => feedback.UserId).HasColumnName("user_id");
            entity.Property(feedback => feedback.WorkId).HasColumnName("work_id");
            entity.Property(feedback => feedback.CompletedOn).HasColumnName("completed_on");
            entity.Property(feedback => feedback.Rating).HasColumnName("rating");
            ConfigureTimestamps(entity);

            // One feedback row per (user, Work); the read pattern (mine, or
            // mine-for-this-Work) never needs anything looser.
            entity.HasIndex(feedback => new { feedback.UserId, feedback.WorkId }).IsUnique();

            // Restrict, matching book_requests: a person's reading history
            // outlives catalog corrections and account deactivation.
            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(feedback => feedback.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Work>()
                .WithMany()
                .HasForeignKey(feedback => feedback.WorkId)
                .OnDelete(DeleteBehavior.Restrict);
        });
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

    private static void ConfigureProviders(ModelBuilder builder)
    {
        builder.Entity<ProviderSetting>(entity =>
        {
            entity.ToTable("provider_settings", "providers");
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

        builder.Entity<ExternalProvider>(entity =>
        {
            entity.ToTable("external_providers", "providers");
            entity.HasKey(provider => provider.Id);
            entity.Property(provider => provider.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(provider => provider.ProviderId).HasColumnName("provider_id").HasMaxLength(64).IsRequired();
            entity.Property(provider => provider.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
            entity.Property(provider => provider.BaseUrl).HasColumnName("base_url").HasMaxLength(1_024).IsRequired();
            entity.Property(provider => provider.IsEnabled).HasColumnName("is_enabled");
            entity.Property(provider => provider.RecheckSchedule).HasColumnName("recheck_schedule").HasConversion<string>().HasMaxLength(32);
            entity.Property(provider => provider.ProtectedApiKey).HasColumnName("protected_api_key").HasMaxLength(4_096);
            entity.Property(provider => provider.ApiKeyFormatVersion).HasColumnName("api_key_format_version");
            entity.Property(provider => provider.ApiKeyHint).HasColumnName("api_key_hint").HasMaxLength(8);
            entity.Property(provider => provider.ApiKeySetAtUtc).HasColumnName("api_key_set_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(provider => provider.CachedProtocolVersion).HasColumnName("cached_protocol_version").HasMaxLength(32);
            entity.Property(provider => provider.CachedCapabilities).HasColumnName("cached_capabilities").HasMaxLength(512);
            entity.Property(provider => provider.CachedEgressPolicy).HasColumnName("cached_egress_policy").HasConversion<string>().HasMaxLength(32);
            entity.Property(provider => provider.EgressPolicyOverride).HasColumnName("overridden_egress_policy").HasConversion<string>().HasMaxLength(32);
            entity.Property(provider => provider.LastTestedAtUtc).HasColumnName("last_tested_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(provider => provider.LastTestSucceeded).HasColumnName("last_test_succeeded");
            entity.Property(provider => provider.LastTestMessage).HasColumnName("last_test_message").HasMaxLength(512);
            entity.Property(provider => provider.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(provider => provider.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(provider => provider.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(provider => provider.Version).HasColumnName("xmin").IsRowVersion();

            entity.HasIndex(provider => provider.ProviderId).IsUnique();
        });

        builder.Entity<PrivateEgressGatewaySettings>(entity =>
        {
            entity.ToTable("private_egress_gateway_settings", "providers");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(settings => settings.IsEnabled).HasColumnName("is_enabled");
            entity.Property(settings => settings.GatewayEndpoint).HasColumnName("gateway_endpoint").HasMaxLength(512);
            entity.Property(settings => settings.LastTestedAtUtc).HasColumnName("last_tested_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.LastTestSucceeded).HasColumnName("last_test_succeeded");
            entity.Property(settings => settings.LastTestMessage).HasColumnName("last_test_message").HasMaxLength(512);
            entity.Property(settings => settings.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(settings => settings.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.Version).HasColumnName("xmin").IsRowVersion();
        });

        builder.Entity<ProviderCatalog>(entity =>
        {
            entity.ToTable("provider_catalogs", "providers");
            entity.HasKey(catalog => catalog.Id);
            entity.Property(catalog => catalog.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(catalog => catalog.Url).HasColumnName("url").HasMaxLength(1_024).IsRequired();
            entity.Property(catalog => catalog.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
            entity.Property(catalog => catalog.IsEnabled).HasColumnName("is_enabled");
            entity.Property(catalog => catalog.CachedEntriesJson).HasColumnName("cached_entries_json").HasColumnType("jsonb");
            entity.Property(catalog => catalog.LastFetchedAtUtc).HasColumnName("last_fetched_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(catalog => catalog.LastFetchSucceeded).HasColumnName("last_fetch_succeeded");
            entity.Property(catalog => catalog.LastFetchMessage).HasColumnName("last_fetch_message").HasMaxLength(512);
            entity.Property(catalog => catalog.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(catalog => catalog.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(catalog => catalog.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(catalog => catalog.Version).HasColumnName("xmin").IsRowVersion();
        });
    }

    private static void ConfigureAcquisition(ModelBuilder builder)
    {
        builder.Entity<AcquisitionJob>(entity =>
        {
            entity.ToTable("acquisition_jobs", "acquisition");
            entity.HasKey(job => job.Id);
            entity.Property(job => job.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(job => job.RequestId).HasColumnName("request_id");
            entity.Property(job => job.MediaType).HasColumnName("media_type").HasConversion<string>().HasMaxLength(32);
            entity.Property(job => job.ProviderId).HasColumnName("provider_id").HasMaxLength(128);
            entity.Property(job => job.EgressPolicy).HasColumnName("egress_policy").HasConversion<string>().HasMaxLength(32);
            entity.Property(job => job.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(job => job.StartedAtUtc).HasColumnName("started_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(job => job.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(job => job.FailureReason).HasColumnName("failure_reason").HasMaxLength(2_000);
            ConfigureTimestamps(entity);

            // The admin queue for outstanding jobs filters/orders by these.
            entity.HasIndex(job => new { job.RequestId, job.Status });

            // Restrict: acquisition history outlives request-record changes, the
            // same rationale as BookRequest's own foreign keys.
            entity.HasOne<BookRequest>()
                .WithMany()
                .HasForeignKey(job => job.RequestId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(job => job.Candidates)
                .WithOne(candidate => candidate.AcquisitionJob)
                .HasForeignKey(candidate => candidate.AcquisitionJobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Navigation(job => job.Candidates).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<AcquisitionCandidate>(entity =>
        {
            entity.ToTable("acquisition_candidates", "acquisition");
            entity.HasKey(candidate => candidate.Id);
            entity.Property(candidate => candidate.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(candidate => candidate.AcquisitionJobId).HasColumnName("acquisition_job_id");
            entity.Property(candidate => candidate.ProviderId).HasColumnName("provider_id").HasMaxLength(128);
            entity.Property(candidate => candidate.ProviderReference).HasColumnName("provider_reference").HasMaxLength(512);
            entity.Property(candidate => candidate.Title).HasColumnName("title").HasMaxLength(1_024);
            entity.Property(candidate => candidate.Author).HasColumnName("author").HasMaxLength(512);
            entity.Property(candidate => candidate.Format).HasColumnName("format").HasMaxLength(32);
            entity.Property(candidate => candidate.SizeBytes).HasColumnName("size_bytes");
            entity.Property(candidate => candidate.Duration).HasColumnName("duration");
            entity.Property(candidate => candidate.BitrateKbps).HasColumnName("bitrate_kbps");
            entity.Property(candidate => candidate.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            entity.Property(candidate => candidate.ConfidenceScore).HasColumnName("confidence_score");
            entity.Property(candidate => candidate.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            ConfigureTimestamps(entity);
        });

        builder.Entity<ProviderAttempt>(entity =>
        {
            entity.ToTable("provider_attempts", "acquisition");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(attempt => attempt.RequestId).HasColumnName("request_id");
            entity.Property(attempt => attempt.RequestFormatId).HasColumnName("request_format_id");
            entity.Property(attempt => attempt.ProviderId).HasColumnName("provider_id").HasMaxLength(128).IsRequired();
            entity.Property(attempt => attempt.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(32);
            entity.Property(attempt => attempt.Summary).HasColumnName("summary").HasMaxLength(512).IsRequired();
            entity.Property(attempt => attempt.AttemptedAtUtc).HasColumnName("attempted_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(attempt => attempt.NextEligibleCheckAtUtc).HasColumnName("next_eligible_check_at_utc").HasColumnType("timestamp with time zone");

            entity.HasIndex(attempt => new { attempt.RequestId, attempt.AttemptedAtUtc });
            entity.HasIndex(attempt => new { attempt.RequestFormatId, attempt.ProviderId, attempt.AttemptedAtUtc });

            entity.HasOne<BookRequest>()
                .WithMany()
                .HasForeignKey(attempt => attempt.RequestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<RequestFormat>()
                .WithMany()
                .HasForeignKey(attempt => attempt.RequestFormatId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MediaAsset>(entity =>
        {
            entity.ToTable("media_assets", "acquisition");
            entity.HasKey(asset => asset.Id);
            entity.Property(asset => asset.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(asset => asset.WorkId).HasColumnName("work_id");
            entity.Property(asset => asset.EditionId).HasColumnName("edition_id");
            entity.Property(asset => asset.MediaType).HasColumnName("media_type").HasConversion<string>().HasMaxLength(32);
            entity.Property(asset => asset.Format).HasColumnName("format").HasMaxLength(32);
            entity.Property(asset => asset.OriginalFilename).HasColumnName("original_filename").HasMaxLength(1_024);
            entity.Property(asset => asset.StoredFilename).HasColumnName("stored_filename").HasMaxLength(256);
            entity.Property(asset => asset.SizeBytes).HasColumnName("size_bytes");
            entity.Property(asset => asset.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(asset => asset.DetectedMimeType).HasColumnName("detected_mime_type").HasMaxLength(128);
            entity.Property(asset => asset.AssociatedRequestFormatId).HasColumnName("associated_request_format_id");
            entity.Property(asset => asset.SourceAcquisitionCandidateId).HasColumnName("source_acquisition_candidate_id");
            entity.Property(asset => asset.StorageState).HasColumnName("storage_state").HasConversion<string>().HasMaxLength(32);
            ConfigureTimestamps(entity);

            // Duplicate-upload detection looks up by (format, checksum); the asset
            // review surfaces will look up an asset by its owning format.
            entity.HasIndex(asset => asset.Sha256);
            entity.HasIndex(asset => asset.AssociatedRequestFormatId);

            entity.HasOne<Work>()
                .WithMany()
                .HasForeignKey(asset => asset.WorkId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<RequestFormat>()
                .WithMany()
                .HasForeignKey(asset => asset.AssociatedRequestFormatId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AcquisitionCandidate>()
                .WithMany()
                .HasForeignKey(asset => asset.SourceAcquisitionCandidateId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureSecurity(ModelBuilder builder)
    {
        builder.Entity<SecurityEvaluation>(entity =>
        {
            entity.ToTable("security_evaluations", "security");
            entity.HasKey(evaluation => evaluation.Id);
            entity.Property(evaluation => evaluation.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(evaluation => evaluation.AssetId).HasColumnName("asset_id");
            entity.Property(evaluation => evaluation.PolicyVersion).HasColumnName("policy_version").HasMaxLength(32);
            entity.Property(evaluation => evaluation.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(evaluation => evaluation.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(evaluation => evaluation.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamp with time zone");

            // The admin asset-review surface looks up the most recent evaluation
            // for one asset.
            entity.HasIndex(evaluation => new { evaluation.AssetId, evaluation.CreatedAtUtc });

            entity.HasOne<MediaAsset>()
                .WithMany()
                .HasForeignKey(evaluation => evaluation.AssetId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(evaluation => evaluation.ScanResults)
                .WithOne()
                .HasForeignKey(result => result.SecurityEvaluationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(evaluation => evaluation.ValidationResults)
                .WithOne()
                .HasForeignKey(result => result.SecurityEvaluationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(evaluation => evaluation.Approvals)
                .WithOne()
                .HasForeignKey(approval => approval.SecurityEvaluationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Navigation(evaluation => evaluation.ScanResults).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.Navigation(evaluation => evaluation.ValidationResults).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.Navigation(evaluation => evaluation.Approvals).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<SecurityScanResult>(entity =>
        {
            entity.ToTable("security_scan_results", "security");
            entity.HasKey(result => result.Id);
            entity.Property(result => result.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(result => result.SecurityEvaluationId).HasColumnName("security_evaluation_id");
            entity.Property(result => result.ScannerId).HasColumnName("scanner_id").HasMaxLength(64);
            entity.Property(result => result.IsRequired).HasColumnName("is_required");
            entity.Property(result => result.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(result => result.ThreatName).HasColumnName("threat_name").HasMaxLength(256);
            entity.Property(result => result.ScannerVersion).HasColumnName("scanner_version").HasMaxLength(64);
            entity.Property(result => result.ScannedAtUtc).HasColumnName("scanned_at_utc").HasColumnType("timestamp with time zone");
        });

        builder.Entity<FormatValidationResult>(entity =>
        {
            entity.ToTable("format_validation_results", "security");
            entity.HasKey(result => result.Id);
            entity.Property(result => result.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(result => result.SecurityEvaluationId).HasColumnName("security_evaluation_id");
            entity.Property(result => result.ValidatorId).HasColumnName("validator_id").HasMaxLength(64);
            entity.Property(result => result.IsValid).HasColumnName("is_valid");
            entity.Property(result => result.Message).HasColumnName("message").HasMaxLength(1_024);
            entity.Property(result => result.ValidatedAtUtc).HasColumnName("validated_at_utc").HasColumnType("timestamp with time zone");
        });

        builder.Entity<Approval>(entity =>
        {
            entity.ToTable("approvals", "security");
            entity.HasKey(approval => approval.Id);
            entity.Property(approval => approval.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(approval => approval.SecurityEvaluationId).HasColumnName("security_evaluation_id");
            entity.Property(approval => approval.Decision).HasColumnName("decision").HasConversion<string>().HasMaxLength(32);
            entity.Property(approval => approval.ActorType).HasColumnName("actor_type").HasConversion<string>().HasMaxLength(32);
            entity.Property(approval => approval.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(approval => approval.PolicyName).HasColumnName("policy_name").HasMaxLength(128);
            entity.Property(approval => approval.Reason).HasColumnName("reason").HasMaxLength(Approval.MaxReasonLength);
            entity.Property(approval => approval.DecidedAtUtc).HasColumnName("decided_at_utc").HasColumnType("timestamp with time zone");
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

    private static void ConfigurePublishing(ModelBuilder builder)
    {
        builder.Entity<CwaSettings>(entity =>
        {
            entity.ToTable("cwa_settings", "publishing");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(settings => settings.IsEnabled).HasColumnName("is_enabled");
            entity.Property(settings => settings.TransportMode).HasColumnName("transport_mode").HasConversion<string>().HasMaxLength(32);
            entity.Property(settings => settings.LocalIngestPath).HasColumnName("local_ingest_path").HasMaxLength(1_024);
            entity.Property(settings => settings.SftpHost).HasColumnName("sftp_host").HasMaxLength(256);
            entity.Property(settings => settings.SftpPort).HasColumnName("sftp_port");
            entity.Property(settings => settings.SftpUsername).HasColumnName("sftp_username").HasMaxLength(256);
            entity.Property(settings => settings.SftpIngestPath).HasColumnName("sftp_ingest_path").HasMaxLength(1_024);
            entity.Property(settings => settings.SftpAuthenticationMode).HasColumnName("sftp_authentication_mode").HasConversion<string>().HasMaxLength(32).HasDefaultValue(CwaSftpAuthenticationMode.PrivateKey);
            entity.Property(settings => settings.ProtectedSftpPrivateKey).HasColumnName("protected_sftp_private_key").HasMaxLength(8_192);
            entity.Property(settings => settings.SftpPrivateKeyFormatVersion).HasColumnName("sftp_private_key_format_version");
            entity.Property(settings => settings.SftpPrivateKeyHint).HasColumnName("sftp_private_key_hint").HasMaxLength(8);
            entity.Property(settings => settings.SftpPrivateKeySetAtUtc).HasColumnName("sftp_private_key_set_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.ProtectedSftpPassphrase).HasColumnName("protected_sftp_passphrase").HasMaxLength(2_048);
            entity.Property(settings => settings.SftpPassphraseFormatVersion).HasColumnName("sftp_passphrase_format_version");
            entity.Property(settings => settings.SftpPassphraseHint).HasColumnName("sftp_passphrase_hint").HasMaxLength(8);
            entity.Property(settings => settings.SftpPassphraseSetAtUtc).HasColumnName("sftp_passphrase_set_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.ProtectedSftpPassword).HasColumnName("protected_sftp_password").HasMaxLength(2_048);
            entity.Property(settings => settings.SftpPasswordFormatVersion).HasColumnName("sftp_password_format_version");
            entity.Property(settings => settings.SftpPasswordHint).HasColumnName("sftp_password_hint").HasMaxLength(8);
            entity.Property(settings => settings.SftpPasswordSetAtUtc).HasColumnName("sftp_password_set_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.SftpHostKeyFingerprint).HasColumnName("sftp_host_key_fingerprint").HasMaxLength(128);
            entity.Property(settings => settings.SftpHostKeyTrustedAtUtc).HasColumnName("sftp_host_key_trusted_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.OpdsBaseUrl).HasColumnName("opds_base_url").HasMaxLength(512);
            entity.Property(settings => settings.OpdsUsername).HasColumnName("opds_username").HasMaxLength(256);
            entity.Property(settings => settings.ProtectedOpdsPassword).HasColumnName("protected_opds_password").HasMaxLength(2_048);
            entity.Property(settings => settings.OpdsPasswordFormatVersion).HasColumnName("opds_password_format_version");
            entity.Property(settings => settings.OpdsPasswordHint).HasColumnName("opds_password_hint").HasMaxLength(8);
            entity.Property(settings => settings.OpdsPasswordSetAtUtc).HasColumnName("opds_password_set_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.LastTestedAtUtc).HasColumnName("last_tested_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.LastTestSucceeded).HasColumnName("last_test_succeeded");
            entity.Property(settings => settings.LastTestMessage).HasColumnName("last_test_message").HasMaxLength(512);
            entity.Property(settings => settings.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(settings => settings.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.Version).HasColumnName("xmin").IsRowVersion();
        });

        builder.Entity<AudiobookshelfSettings>(entity =>
        {
            entity.ToTable("audiobookshelf_settings", "publishing");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(settings => settings.IsEnabled).HasColumnName("is_enabled");
            entity.Property(settings => settings.BaseUrl).HasColumnName("base_url").HasMaxLength(512);
            entity.Property(settings => settings.LibraryId).HasColumnName("library_id").HasMaxLength(128);
            entity.Property(settings => settings.FolderId).HasColumnName("folder_id").HasMaxLength(128);
            entity.Property(settings => settings.ProtectedApiToken).HasColumnName("protected_api_token").HasMaxLength(2_048);
            entity.Property(settings => settings.ApiTokenFormatVersion).HasColumnName("api_token_format_version");
            entity.Property(settings => settings.ApiTokenHint).HasColumnName("api_token_hint").HasMaxLength(8);
            entity.Property(settings => settings.ApiTokenSetAtUtc).HasColumnName("api_token_set_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.LastTestedAtUtc).HasColumnName("last_tested_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.LastTestSucceeded).HasColumnName("last_test_succeeded");
            entity.Property(settings => settings.LastTestMessage).HasColumnName("last_test_message").HasMaxLength(512);
            entity.Property(settings => settings.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(settings => settings.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.Version).HasColumnName("xmin").IsRowVersion();
        });

        builder.Entity<LibraryImport>(entity =>
        {
            entity.ToTable("library_imports", "publishing");
            entity.HasKey(import => import.Id);
            entity.Property(import => import.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(import => import.AssetId).HasColumnName("asset_id");
            entity.Property(import => import.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(import => import.ExternalBookId).HasColumnName("external_book_id").HasMaxLength(256);
            entity.Property(import => import.FailureReason).HasColumnName("failure_reason").HasMaxLength(2_000);
            entity.Property(import => import.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(import => import.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(import => import.Version).HasColumnName("xmin").IsRowVersion();

            entity.HasIndex(import => import.AssetId);

            entity.HasOne<MediaAsset>()
                .WithMany()
                .HasForeignKey(import => import.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Delivery>(entity =>
        {
            entity.ToTable("deliveries", "publishing");
            entity.HasKey(delivery => delivery.Id);
            entity.Property(delivery => delivery.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(delivery => delivery.AssetId).HasColumnName("asset_id");
            entity.Property(delivery => delivery.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(delivery => delivery.ExternalItemId).HasColumnName("external_item_id").HasMaxLength(256);
            entity.Property(delivery => delivery.FailureReason).HasColumnName("failure_reason").HasMaxLength(2_000);
            entity.Property(delivery => delivery.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(delivery => delivery.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(delivery => delivery.Version).HasColumnName("xmin").IsRowVersion();

            entity.HasIndex(delivery => delivery.AssetId);

            entity.HasOne<MediaAsset>()
                .WithMany()
                .HasForeignKey(delivery => delivery.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePolicy(ModelBuilder builder)
    {
        builder.Entity<AcquisitionPolicySettings>(entity =>
        {
            entity.ToTable("acquisition_policy_settings", "policy");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(settings => settings.DefaultProfileId).HasColumnName("default_profile_id").HasMaxLength(64).IsRequired();
            entity.Property(settings => settings.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(settings => settings.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
            entity.Property(settings => settings.Version).HasColumnName("xmin").IsRowVersion();
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
