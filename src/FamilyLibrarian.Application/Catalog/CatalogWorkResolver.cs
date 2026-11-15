using System.Globalization;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;

namespace FamilyLibrarian.Application.Catalog;

public sealed class CatalogWorkResolver(
    IActiveMetadataProviderResolver providers,
    ICatalogRepository catalogRepository,
    ICatalogWriteScope writeScope,
    NotificationService notifications,
    IClock clock,
    ICatalogResolutionDiagnostics? diagnostics = null)
{
    /// <summary>
    /// Resolves a provider record to a catalog Work. A reference that is already saved is returned with no provider call and no
    /// write. Otherwise only an active provider is asked, once, outside any transaction; the catalog is then changed inside the
    /// catalog write scope, which serializes writers across processes and retries a recoverable conflict at most once.
    /// </summary>
    /// <exception cref="CatalogResolutionException">An expected, typed failure; nothing was written.</exception>
    public async Task<CatalogWorkResolution> ResolveAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken)
    {
        ValidateProviderReference(providerId, externalId);

        // A saved reference never depends on the source still being enabled or reachable.
        var existingByReference = await catalogRepository.FindWorkByExternalReferenceAsync(
            providerId,
            externalId,
            cancellationToken);
        if (existingByReference is not null)
        {
            return new CatalogWorkResolution(existingByReference, false);
        }

        // Enablement is the only authority to call a provider. Registration is consulted afterwards, to tell "no such source"
        // from "that source is switched off"; it never lets a disabled provider be invoked.
        var provider = await providers.FindActiveProviderAsync(providerId, cancellationToken);
        if (provider is null)
        {
            throw providers.IsRegistered(providerId)
                ? new CatalogResolutionException(CatalogResolutionFault.ProviderNotEnabled, $"The metadata provider '{providerId}' is not enabled.")
                : new CatalogResolutionException(CatalogResolutionFault.UnknownProvider, $"The metadata provider '{providerId}' is not installed.");
        }

        // The provider call is slow and touches no catalog data, so it stays outside the write scope, and it is never repeated
        // by a retry: the candidate is immutable evidence for every attempt.
        var candidate = await provider.GetDetailsAsync(externalId, cancellationToken)
            ?? throw new CatalogResolutionException(
                CatalogResolutionFault.CandidateNotFound, $"The metadata candidate '{providerId}/{externalId}' was not found.");

        var write = await writeScope.ExecuteAsync(
            (session, token) => MutateAsync(session, provider.Id, candidate, token),
            VerifyAsync,
            cancellationToken);

        var attempt = write.Status switch
        {
            CatalogWriteStatus.Committed or CatalogWriteStatus.Recovered => write.Value!,
            CatalogWriteStatus.Busy => throw new CatalogResolutionException(
                CatalogResolutionFault.Busy, "The catalog is busy with another change."),
            CatalogWriteStatus.Conflict => throw new CatalogResolutionException(
                CatalogResolutionFault.Conflict, "The catalog changed at the same time; nothing was added."),
            CatalogWriteStatus.OutcomeUnknown => throw new CatalogResolutionException(
                CatalogResolutionFault.OutcomeUnknown, "The result of the catalog change could not be confirmed."),
            _ => throw new CatalogResolutionException(
                CatalogResolutionFault.Unavailable, "The catalog database is temporarily unavailable.")
        };

        if (attempt.Fault is { } fault)
        {
            throw new CatalogResolutionException(fault, fault == CatalogResolutionFault.IsbnCollision
                ? "The candidate's ISBNs belong to more than one saved Work, so it was not attached to any."
                : "The candidate's ISBN is contested by a concurrent change, so it was not attached.");
        }

        var resolution = attempt.Resolution!;
        if (write.Status == CatalogWriteStatus.Committed && resolution.WasCreated)
        {
            // Only after the catalog write actually commits: a notification for a Work that failed to save would be worse than
            // not sending one. A recovered result is never notified, so a replay cannot duplicate one.
            diagnostics?.Report(resolution.Work.Id, resolution.Diagnostics);
            await NotifyFollowersAsync(resolution.Work, attempt, cancellationToken);
        }

        return resolution;
    }

    public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
        catalogRepository.GetWorkAsync(workId, cancellationToken);

    public Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(
        Guid workId,
        CancellationToken cancellationToken) =>
        catalogRepository.GetWorkSourcesAsync(workId, cancellationToken);

    /// <summary>One attempt, run inside the write scope: every read and write goes through <paramref name="session"/>.</summary>
    private async Task<CatalogAttempt> MutateAsync(
        ICatalogWriteSession session,
        string providerId,
        BookCandidate candidate,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;

        // Another writer may have created this reference while this one waited for the lock.
        var createdMeanwhile = await catalog.FindWorkByExternalReferenceAsync(providerId, candidate.ExternalId, cancellationToken);
        if (createdMeanwhile is not null)
        {
            return CatalogAttempt.Found(createdMeanwhile);
        }

        var observedAtUtc = clock.UtcNow;
        var isbn13s = candidate.Editions
            .Select(edition => edition.Isbn13)
            .Where(isbn13 => !string.IsNullOrWhiteSpace(isbn13))
            .Select(isbn13 => isbn13!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingByIsbn = isbn13s.Length == 0
            ? []
            : await catalog.ListWorksByIsbn13Async(isbn13s, cancellationToken);

        if (existingByIsbn.Count > 1)
        {
            // Which Work the book belongs to is not knowable from an ISBN set that spans several: attach to none.
            return CatalogAttempt.Failed(CatalogResolutionFault.IsbnCollision);
        }

        if (existingByIsbn.Count == 1)
        {
            if (session.RetryCause == CatalogWriteRetryCause.UniqueViolation)
            {
                // The previous attempt lost an ISBN race to a writer outside the lock. That the ISBN now has an owner is not
                // proof the owner is this book, so the contested ISBN is not adopted.
                return CatalogAttempt.Failed(CatalogResolutionFault.Conflict);
            }

            var existing = existingByIsbn[0];
            existing.SetCatalogLength(candidate.PageCount is > 0 ? candidate.PageCount : null, null, providerId, observedAtUtc);
            catalog.AddExternalReference(new ExternalReference(
                providerId,
                ExternalReferenceEntityType.Work,
                existing.Id,
                candidate.ExternalId,
                observedAtUtc));
            await catalog.SaveChangesAsync(cancellationToken);
            return CatalogAttempt.Attached(existing, providerId, candidate.ExternalId);
        }

        var work = new Work(
            candidate.Title,
            candidate.Description,
            candidate.CoverUrl,
            candidate.PublicationDate,
            candidate.PublicationDate is null ? PublicationStatus.Unknown : PublicationStatus.Published,
            observedAtUtc);

        var authorNotices = new List<AuthorNotice>();
        var seriesNotices = new List<SeriesNotice>();
        var ambiguousAuthors = new List<AmbiguousAuthorCredit>();
        var omittedSeries = new List<OmittedSeriesMembership>();

        await AddAuthorsAsync(session, work, candidate.Authors, observedAtUtc, authorNotices, ambiguousAuthors, cancellationToken);
        work.SetCatalogLength(candidate.PageCount is > 0 ? candidate.PageCount : null, null, providerId, observedAtUtc);
        AddEditions(work, candidate.Editions, observedAtUtc);
        await AddSeriesEntriesAsync(session, work, candidate.Series, observedAtUtc, seriesNotices, omittedSeries, cancellationToken);
        catalog.AddWork(work);
        catalog.AddExternalReference(new ExternalReference(
            providerId,
            ExternalReferenceEntityType.Work,
            work.Id,
            candidate.ExternalId,
            observedAtUtc));
        await catalog.SaveChangesAsync(cancellationToken);

        return CatalogAttempt.Created(
            work,
            providerId,
            candidate.ExternalId,
            authorNotices,
            seriesNotices,
            new CatalogResolutionDiagnostics(ambiguousAuthors, omittedSeries));
    }

    /// <summary>
    /// After a commit whose outcome is unknown: the operation happened exactly when its provider reference now exists. An
    /// attempt that wrote nothing has nothing to verify.
    /// </summary>
    private static async Task<CatalogAttempt?> VerifyAsync(
        ICatalogWriteSession session,
        CatalogAttempt attempted,
        CancellationToken cancellationToken)
    {
        if (!attempted.Wrote)
        {
            return attempted;
        }

        var committed = await session.Catalog.FindWorkByExternalReferenceAsync(
            attempted.ReferenceProviderId!, attempted.ReferenceExternalId!, cancellationToken);
        return committed is null ? null : CatalogAttempt.Found(committed);
    }

    private static async Task AddAuthorsAsync(
        ICatalogWriteSession session,
        Work work,
        IReadOnlyList<string> names,
        DateTimeOffset observedAtUtc,
        List<AuthorNotice> followerNotices,
        List<AmbiguousAuthorCredit> ambiguousCredits,
        CancellationToken cancellationToken)
    {
        foreach (var name in names
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Select(name => name.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Select((name, ordinal) => (name, ordinal)))
        {
            var normalizedName = CatalogText.NormalizeForMatch(name.name);
            var namesakes = await session.Catalog.ListAuthorsByNormalizedNameAsync(normalizedName, cancellationToken);

            if (namesakes.Count == 1)
            {
                // Exactly one candidate keeps today's name-based binding (unverified, and unchanged by this hardening).
                var author = namesakes[0];
                work.AddAuthor(author, name.ordinal);
                var followers = await session.Follows.ListFollowersAsync(
                    FollowSubjectType.Author, author.Id, cancellationToken);
                followerNotices.AddRange(followers.Select(follower => new AuthorNotice(follower.UserId, author.Id, author.CanonicalName)));
                continue;
            }

            // None: an author nobody could have followed yet. Several: a name alone proves no one of them, so the credit gets
            // its own Author for this Work, no namesake gains a Work, and no follower of a namesake is told anything.
            var created = new Author(name.name, null, observedAtUtc);
            session.Catalog.AddAuthor(created);
            work.AddAuthor(created, name.ordinal);
            if (namesakes.Count > 1)
            {
                ambiguousCredits.Add(new AmbiguousAuthorCredit(
                    created.CanonicalName, created.Id, namesakes.Select(namesake => namesake.Id).ToArray()));
            }
        }
    }

    private static void AddEditions(
        Work work,
        IReadOnlyList<BookEditionCandidate> candidates,
        DateTimeOffset observedAtUtc)
    {
        foreach (var candidate in candidates.Where(candidate =>
                     !string.IsNullOrWhiteSpace(candidate.Title)))
        {
            work.AddEdition(new Edition(
                work.Id,
                candidate.Title,
                ToEditionFormat(candidate.Format),
                candidate.Isbn13,
                candidate.PublicationDate,
                observedAtUtc));
        }
    }

    private static async Task AddSeriesEntriesAsync(
        ICatalogWriteSession session,
        Work work,
        IReadOnlyList<BookSeriesCandidate> candidates,
        DateTimeOffset observedAtUtc,
        List<SeriesNotice> followerNotices,
        List<OmittedSeriesMembership> omittedMemberships,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates.Where(candidate =>
                     !string.IsNullOrWhiteSpace(candidate.Name)))
        {
            var normalizedName = CatalogText.NormalizeForMatch(candidate.Name);
            var namesakes = await session.Catalog.ListSeriesByNormalizedNameAsync(normalizedName, cancellationToken);

            if (namesakes.Count > 1)
            {
                // Which of several same-name series this book belongs to is not knowable from the name. No membership is
                // created and no completion flag applied; the omission is reported, not silently dropped.
                omittedMemberships.Add(new OmittedSeriesMembership(
                    candidate.Name.Trim(), namesakes.Select(namesake => namesake.Id).ToArray()));
                continue;
            }

            // A series nobody could have followed yet (this is the first time FL has ever seen it) has no followers to look up.
            var series = namesakes.Count == 1 ? namesakes[0] : null;
            var isNewSeries = series is null;
            if (series is null)
            {
                series = new Series(
                    candidate.Name,
                    candidate.IsCompleted ? SeriesStatus.Completed : SeriesStatus.Unknown,
                    observedAtUtc);
                session.Catalog.AddSeries(series);
            }
            else if (candidate.IsCompleted)
            {
                series.MarkCompleted(observedAtUtc);
            }

            work.AddSeriesEntry(new SeriesEntry(
                series,
                work,
                candidate.PositionLabel,
                candidate.PositionSort ?? TryParsePosition(candidate.PositionLabel),
                candidate.IsPrimary,
                observedAtUtc));

            if (!isNewSeries)
            {
                var followers = await session.Follows.ListFollowersAsync(
                    FollowSubjectType.Series, series.Id, cancellationToken);
                followerNotices.AddRange(followers.Select(follower => new SeriesNotice(follower.UserId, series.Id, series.Name)));
            }
        }
    }

    /// <summary>
    /// Best-effort: a notification failure must never undo or fail an
    /// otherwise-successful catalog resolution a request is waiting on.
    /// </summary>
    private async Task NotifyFollowersAsync(
        Work work,
        CatalogAttempt attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var notice in attempt.AuthorNotices)
            {
                await notifications.RecordAuthorWorkAddedAsync(
                    notice.FollowerUserId, notice.AuthorId, notice.AuthorName, work.Id, work.CanonicalTitle, cancellationToken);
            }

            foreach (var notice in attempt.SeriesNotices)
            {
                await notifications.RecordSeriesEntryAddedAsync(
                    notice.FollowerUserId, notice.SeriesId, notice.SeriesName, work.Id, work.CanonicalTitle, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Swallowed deliberately -- see the summary above.
        }
    }

    private static EditionFormat ToEditionFormat(string format) =>
        format.Trim().ToUpperInvariant() switch
        {
            "HARDCOVER" => EditionFormat.Hardcover,
            "PAPERBACK" => EditionFormat.Paperback,
            "EBOOK" or "E-BOOK" => EditionFormat.Ebook,
            "AUDIOBOOK" or "AUDIO BOOK" => EditionFormat.Audiobook,
            "" or "UNKNOWN FORMAT" => EditionFormat.Unknown,
            _ => EditionFormat.Other
        };

    private static decimal? TryParsePosition(string? positionLabel) =>
        decimal.TryParse(
            positionLabel,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var position)
                ? position
                : null;

    private static void ValidateProviderReference(string providerId, string externalId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 128 ||
            string.IsNullOrWhiteSpace(externalId) || externalId.Length > 512)
        {
            throw new ArgumentException("The provider reference is invalid.");
        }
    }

    private sealed record AuthorNotice(Guid FollowerUserId, Guid AuthorId, string AuthorName);

    private sealed record SeriesNotice(Guid FollowerUserId, Guid SeriesId, string SeriesName);

    /// <summary>
    /// What one write attempt decided. Attempt-local by construction: the notices and the entities belong to the attempt that
    /// produced them, and an abandoned attempt's instance is simply discarded.
    /// </summary>
    private sealed record CatalogAttempt(
        CatalogWorkResolution? Resolution,
        CatalogResolutionFault? Fault,
        bool Wrote,
        string? ReferenceProviderId,
        string? ReferenceExternalId,
        IReadOnlyList<AuthorNotice> AuthorNotices,
        IReadOnlyList<SeriesNotice> SeriesNotices)
    {
        public static CatalogAttempt Found(Work work) =>
            new(new CatalogWorkResolution(work, false) { Outcome = CatalogResolutionOutcome.Existing }, null, false, null, null, [], []);

        public static CatalogAttempt Failed(CatalogResolutionFault fault) =>
            new(null, fault, false, null, null, [], []);

        public static CatalogAttempt Attached(Work work, string providerId, string externalId) =>
            new(new CatalogWorkResolution(work, false) { Outcome = CatalogResolutionOutcome.Attached }, null, true, providerId, externalId, [], []);

        public static CatalogAttempt Created(
            Work work,
            string providerId,
            string externalId,
            IReadOnlyList<AuthorNotice> authorNotices,
            IReadOnlyList<SeriesNotice> seriesNotices,
            CatalogResolutionDiagnostics resolutionDiagnostics) =>
            new(
                new CatalogWorkResolution(work, true) { Outcome = CatalogResolutionOutcome.Created, Diagnostics = resolutionDiagnostics },
                null, true, providerId, externalId, authorNotices, seriesNotices);
    }
}

public enum CatalogResolutionOutcome
{
    /// <summary>The Work was already saved; nothing was written.</summary>
    Existing,

    /// <summary>A new Work was created.</summary>
    Created,

    /// <summary>The provider reference was added to an existing Work that shares one of its ISBNs.</summary>
    Attached
}

/// <param name="Work">The resolved Work. Loaded with its authors, editions and series entries; safe to read after the write scope ended.</param>
/// <param name="WasCreated">True only for the attempt that committed a new Work.</param>
public sealed record CatalogWorkResolution(Work Work, bool WasCreated)
{
    public CatalogResolutionOutcome Outcome { get; init; } = CatalogResolutionOutcome.Existing;

    /// <summary>Credits and memberships that could not be bound to an existing record, for the caller and tests.</summary>
    public CatalogResolutionDiagnostics Diagnostics { get; init; } = CatalogResolutionDiagnostics.None;
}

public sealed record CatalogResolutionDiagnostics(
    IReadOnlyList<AmbiguousAuthorCredit> AmbiguousAuthors,
    IReadOnlyList<OmittedSeriesMembership> OmittedSeriesMemberships)
{
    public static CatalogResolutionDiagnostics None { get; } = new([], []);

    public bool IsEmpty => AmbiguousAuthors.Count == 0 && OmittedSeriesMemberships.Count == 0;
}

/// <summary>An author credit that matched several existing Authors by name, so it received its own new Author instead.</summary>
public sealed record AmbiguousAuthorCredit(string ReportedName, Guid NewAuthorId, IReadOnlyList<Guid> NamesakeAuthorIds);

/// <summary>A series membership that matched several existing Series by name, so no membership was created.</summary>
public sealed record OmittedSeriesMembership(string ReportedName, IReadOnlyList<Guid> NamesakeSeriesIds);

/// <summary>Receives what a committed creation could not bind, so it can be logged without the Application layer owning a logger.</summary>
public interface ICatalogResolutionDiagnostics
{
    void Report(Guid workId, CatalogResolutionDiagnostics diagnostics);
}

public enum CatalogResolutionFault
{
    /// <summary>No installed provider has this id (HTTP 404).</summary>
    UnknownProvider,

    /// <summary>The provider is installed but switched off, so it was not asked (HTTP 409).</summary>
    ProviderNotEnabled,

    /// <summary>The provider has no such record (HTTP 404).</summary>
    CandidateNotFound,

    /// <summary>The candidate's ISBNs belong to more than one saved Work; nothing was written (HTTP 409).</summary>
    IsbnCollision,

    /// <summary>A concurrent writer contested the rows and no safe completion exists; nothing was written (HTTP 409).</summary>
    Conflict,

    /// <summary>The catalog write lock was not available in time; nothing was written (HTTP 503, retryable).</summary>
    Busy,

    /// <summary>The database could not be reached before anything could commit (HTTP 503, retryable).</summary>
    Unavailable,

    /// <summary>The commit's outcome could not be established; the change may or may not exist (HTTP 503, retryable).</summary>
    OutcomeUnknown
}

/// <summary>An expected, deliberately mapped resolution failure. Programming and storage faults are not wrapped in this type.</summary>
public sealed class CatalogResolutionException(CatalogResolutionFault fault, string message) : InvalidOperationException(message)
{
    public CatalogResolutionFault Fault { get; } = fault;
}
