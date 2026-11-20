using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Answers;

/// <summary>
/// Implements <see cref="ICatalogAnswerService"/> over the shared reads: saved evidence projections, the active-provider
/// discovery operations, and the optional budgeted seed read, all under one server-owned journey allowance, one per-user admission
/// and one overall deadline. The pure selection rules live in <see cref="CatalogAnswerPolicy"/>; this class only gathers evidence,
/// accounts for every read and states honestly what was and was not checked.
/// </summary>
/// <remarks>
/// <para>
/// One allowance. A lookup that has to find an anchor first (seed search, then the book's own series claims, then a selected series
/// page) charges every one of those reads to the same journey as the web discovery routes, behind the same admission limiter, so
/// two transports cannot multiply the allowance. Cached, retained-context and rendering follow-ups make no provider call.
/// </para>
/// <para>
/// Never: a native id guessed from a name, a write-resolver call, a second provider registry, a concrete-provider cast, an
/// unbudgeted fallback read, speculative fan-out to every source, or a timeout/failure reported as "nothing found". The target is
/// one eligible source; any other is an explicit later choice (<see cref="CatalogAnswerRequest.ProviderId"/>). EF work is sequential
/// on the scoped context, and every awaited provider read finishes before this scope can be disposed.
/// </para>
/// </remarks>
public sealed class CatalogAnswerService(
    ISavedCatalogEvidenceRepository saved,
    ICatalogBrowseRepository browse,
    IActiveMetadataProviderResolver providers,
    CatalogAnswerContextStore contexts,
    CatalogDiscoveryJourneys journeys,
    CatalogExpansionLimiter limiter,
    IClock clock,
    TimeProvider timeProvider,
    CatalogAnswerOptions options) : ICatalogAnswerService
{
    public async Task<CatalogAnswer> AnswerAsync(
        CatalogAnswerCaller caller, CatalogAnswerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var run = new Run(caller, request, clock.UtcNow, timeProvider.GetTimestamp());
        using var deadline = new CancellationTokenSource(options.Deadline, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var answer = await RunAsync(run, linked.Token);
            // A read that does not observe cancellation can finish after the deadline. Only a cancellation signal says time is up, so
            // it is checked once more here: an answer completed past the boundary is a time-out, never a success. A caller's own
            // cancellation surfaces from this same check as the cancellation it is.
            linked.Token.ThrowIfCancellationRequested();
            return answer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The overall deadline fired, not the caller: a qualified time-out, never an empty answer.
            run.Reports.Add(new CatalogSourceReport(run.LastProviderId ?? "answer", CatalogSourceState.TimedOut, CatalogAnswerCodes.DeadlineReached, null));
            return Compose(run, CatalogAnswerOutcome.TimedOut, subject: run.Request.Subject, reasons: [CatalogAnswerCodes.DeadlineReached]);
        }
        finally
        {
            run.Permit?.Dispose();
        }
    }

    // ===================================================================================================== edition titles

    public async Task<CatalogEditionsAnswer> EditionsAsync(
        CatalogAnswerCaller caller, Guid contextRootId, CatalogSubjectRef entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(entry);

        // The retained window is the selection: the Work is resolved from this caller's own root and checked against what it holds,
        // never supplied blind by a client. The request here only carries the operation label a Run needs; no answer is composed from it.
        var run = new Run(
            caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.SeriesMembers }, clock.UtcNow, timeProvider.GetTimestamp());
        using var deadline = new CancellationTokenSource(options.Deadline, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var answer = await EditionsCoreAsync(run, contextRootId, entry, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return answer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EditionsOf(run, CatalogAnswerOutcome.TimedOut, null, string.Empty, [], [CatalogAnswerCodes.DeadlineReached]);
        }
        finally
        {
            run.Permit?.Dispose();
        }
    }

    private async Task<CatalogEditionsAnswer> EditionsCoreAsync(
        Run run, Guid rootId, CatalogSubjectRef entry, CancellationToken ct)
    {
        var lease = contexts.TryGetRoot(run.Caller, rootId, run.Now);
        var window = contexts.TryGetWindow(run.Caller, rootId, run.Now);
        if (lease is null || window is null)
        {
            // Another reader's, another room's, expired or evicted: one answer, and nothing is fetched or retained.
            return EditionsOf(run, CatalogAnswerOutcome.ContextExpired, null, string.Empty, [], [CatalogAnswerCodes.ContextExpiredReason]);
        }

        run.Root = lease;
        var matched = window.Entries.FirstOrDefault(retained => SameEntry(retained, entry));
        if (matched?.Source is not { } work)
        {
            // A saved-only record, or one this window does not hold (stale, replayed or from another conversation): nothing expands.
            return EditionsOf(run, CatalogAnswerOutcome.Unsupported, null, matched?.Title ?? string.Empty, [], [CatalogAnswerCodes.OperationNotSupported]);
        }

        var workTitle = matched.Title;
        if (work.Kind != ExternalReferenceEntityType.Work)
        {
            // An edition record has no Work-level edition list to expand.
            return EditionsOf(run, CatalogAnswerOutcome.Unsupported, work, workTitle, [], [CatalogAnswerCodes.OperationNotSupported]);
        }

        var active = await providers.FindActiveProviderAsync(work.ProviderId, ct);
        if (active is null)
        {
            var reason = providers.IsRegistered(work.ProviderId) ? "source-disabled" : "source-removed";
            return EditionsOf(run, CatalogAnswerOutcome.Unavailable, work, workTitle, [], [reason]);
        }

        if (active is not IWorkEditionTitlesProvider editions)
        {
            return EditionsOf(run, CatalogAnswerOutcome.Unsupported, work, workTitle, [], [CatalogAnswerCodes.EditionTitlesUnavailable]);
        }

        // Resolving the source awaited a settings read: the root, journey and admission are checked against a fresh instant first.
        if (Enter(run, work.ProviderId) is { } refusal)
        {
            return EditionsOf(run, refusal.Outcome, work, workTitle, [], refusal.Reasons, run.RetryAfter);
        }

        var operation = run.Journey!.BeginOperation(DiscoveryOperation.GetWorkEditions, work.ProviderId, work);
        if (operation is null)
        {
            return EditionsOf(run, CatalogAnswerOutcome.ContextExpired, work, workTitle, [], [DiscoveryCodes.JourneyExpired]);
        }

        run.ProviderReads++;
        run.LastProviderId = work.ProviderId;
        var read = await editions.GetWorkEditionTitlesAsync(work, operation, ct);
        run.Usages.Add(read.Usage);
        ct.ThrowIfCancellationRequested();

        if (read.Outcome is not (DiscoveryOutcome.Ok or DiscoveryOutcome.Empty))
        {
            return EditionsOf(run, OutcomeOf(read.Outcome), work, workTitle, [], read.Notes);
        }

        if (read.Value is null || read.Value.Editions.Count == 0)
        {
            // The source answered and listed no edition: no verified match in what it returned, never a global absence.
            return EditionsOf(run, CatalogAnswerOutcome.NoVerifiedMatch, work, workTitle, [], [CatalogAnswerCodes.NoEntries]);
        }

        var titles = read.Value.Editions
            .Where(edition => !string.IsNullOrWhiteSpace(edition.Title))
            .Take(options.MaxEditionTitles)
            .Select(edition => new CatalogEditionTitle(
                edition.EditionId, edition.Title.Trim(), string.IsNullOrWhiteSpace(edition.Language) ? null : edition.Language.Trim(), edition.SourceField))
            .ToArray();
        if (titles.Length == 0)
        {
            return EditionsOf(run, CatalogAnswerOutcome.NoVerifiedMatch, work, workTitle, [], [CatalogAnswerCodes.NoEntries]);
        }

        // One page of a Work's editions is never all of them, so the list is qualified whatever the source reported.
        return EditionsOf(run, CatalogAnswerOutcome.Qualified, work, workTitle, titles, []);
    }

    /// <summary>True when a retained entry is the record the caller selected: the saved Work or the native source reference, never a title.</summary>
    private static bool SameEntry(CatalogAnswerEntry retained, CatalogSubjectRef subject)
    {
        if (subject.SavedId is { } saved) return retained.SavedWorkId == saved;
        return subject.Native is { } native && retained.Source == native;
    }

    private CatalogEditionsAnswer EditionsOf(
        Run run,
        CatalogAnswerOutcome outcome,
        SourceRef? work,
        string workTitle,
        IReadOnlyList<CatalogEditionTitle> editions,
        IReadOnlyList<string> reasons,
        TimeSpan retryAfter = default)
    {
        var usage = new CatalogAnswerUsage(
            run.ProviderReads,
            run.Usages.Sum(item => item.Attempts),
            run.Usages.Sum(item => item.Retries),
            run.Usages.Sum(item => item.Pages),
            run.Usages.Sum(item => item.Items),
            timeProvider.GetElapsedTime(run.Started));
        return new CatalogEditionsAnswer(
            outcome,
            work,
            workTitle,
            editions,
            outcome == CatalogAnswerOutcome.Qualified ? [CatalogAnswerCodes.ListNotExhaustive] : [],
            reasons,
            usage,
            work?.ProviderId,
            outcome is CatalogAnswerOutcome.Refused or CatalogAnswerOutcome.Throttled
                ? (retryAfter == TimeSpan.Zero ? run.RetryAfter : retryAfter)
                : TimeSpan.Zero);
    }

    private static void Validate(CatalogAnswerRequest request)
    {
        if (!Enum.IsDefined(request.Operation)) throw new ArgumentOutOfRangeException(nameof(request), "The operation is not defined.");
        if (request.Operation == CatalogAnswerOperation.Position && request.Position is null)
        {
            throw new ArgumentException("A position question needs the position asked for.", nameof(request));
        }

        if (request.Operation == CatalogAnswerOperation.AfterAnchor && request.Anchor is null && request.Position is null && request.ContextRootId is null)
        {
            throw new ArgumentException("A next-in-order question needs an anchor book or position.", nameof(request));
        }
    }

    private async Task<CatalogAnswer> RunAsync(Run run, CancellationToken ct)
    {
        var request = run.Request;
        if (request.ContextRootId is { } rootId)
        {
            return await FollowUpAsync(run, rootId, ct);
        }

        return await FreshAsync(run, ct);
    }

    // ==================================================================================================== fresh lookups

    private Task<CatalogAnswer> FreshAsync(Run run, CancellationToken ct) => run.Request.Operation switch
    {
        CatalogAnswerOperation.PublicationForTitle => TitleAsync(run, ct),
        CatalogAnswerOperation.AuthorWorks or CatalogAnswerOperation.AuthorSeries => AuthorAsync(run, ct),
        // Latest, upcoming and next release are publication questions that a selected series or a selected author can ask. The
        // subject the caller selected decides the read; an author is never silently turned into a series (or the reverse).
        CatalogAnswerOperation.LatestPublished or CatalogAnswerOperation.UpcomingReleases or CatalogAnswerOperation.NextRelease
            when run.Request.Subject?.Kind == CatalogSubjectKind.Author => AuthorAsync(run, ct),
        _ => SeriesAsync(run, ct)
    };

    /// <summary>Which subjects an operation can be asked about. Position and next-in-order are series questions only.</summary>
    private static bool AppliesTo(CatalogAnswerOperation operation, CatalogSubjectKind kind) => operation switch
    {
        CatalogAnswerOperation.LatestPublished or CatalogAnswerOperation.UpcomingReleases or CatalogAnswerOperation.NextRelease =>
            kind is CatalogSubjectKind.Series or CatalogSubjectKind.Author,
        CatalogAnswerOperation.Position or CatalogAnswerOperation.AfterAnchor => kind == CatalogSubjectKind.Series,
        CatalogAnswerOperation.AuthorWorks or CatalogAnswerOperation.AuthorSeries => kind == CatalogSubjectKind.Author,
        CatalogAnswerOperation.PublicationForTitle => kind == CatalogSubjectKind.Title,
        CatalogAnswerOperation.SeriesMembers => kind == CatalogSubjectKind.Series,
        _ => false
    };

    /// <summary>The read an operation needs for a subject. A retained window is reusable only when it holds that same read.</summary>
    private static CatalogReadKind ReadKindFor(CatalogAnswerOperation operation, CatalogSubjectKind kind) => kind switch
    {
        CatalogSubjectKind.Series => CatalogReadKind.SeriesMembers,
        CatalogSubjectKind.Author => operation == CatalogAnswerOperation.AuthorSeries ? CatalogReadKind.AuthorSeries : CatalogReadKind.AuthorWorks,
        CatalogSubjectKind.Title => CatalogReadKind.TitleSearch,
        _ => CatalogReadKind.Choices
    };

    private async Task<CatalogAnswer> SeriesAsync(Run run, CancellationToken ct)
    {
        var subject = run.Request.Subject;
        if (subject is null)
        {
            return string.IsNullOrWhiteSpace(run.Request.Text)
                ? Compose(run, CatalogAnswerOutcome.InsufficientEvidence, reasons: [CatalogAnswerCodes.SubjectRequired])
                : await ChoicesAsync(run, CatalogSubjectKind.Series, run.Request.Text.Trim(), ct);
        }

        if (subject.Kind != CatalogSubjectKind.Series)
        {
            return Compose(run, CatalogAnswerOutcome.InsufficientEvidence, subject, reasons: [CatalogAnswerCodes.SubjectInvalid]);
        }

        var page = subject.SavedId is { } savedId
            ? await LoadSavedSeriesAsync(run, savedId, subject, ct)
            : await LoadSeriesMembersAsync(run, subject, null, ct);
        return Evaluate(run, subject, page, null, ct);
    }

    private async Task<CatalogAnswer> AuthorAsync(Run run, CancellationToken ct)
    {
        var subject = run.Request.Subject;
        if (subject is null)
        {
            return string.IsNullOrWhiteSpace(run.Request.Text)
                ? Compose(run, CatalogAnswerOutcome.InsufficientEvidence, reasons: [CatalogAnswerCodes.SubjectRequired])
                : await ChoicesAsync(run, CatalogSubjectKind.Author, run.Request.Text.Trim(), ct);
        }

        if (subject.Kind != CatalogSubjectKind.Author)
        {
            return Compose(run, CatalogAnswerOutcome.InsufficientEvidence, subject, reasons: [CatalogAnswerCodes.SubjectInvalid]);
        }

        if (subject.SavedId is { } savedId)
        {
            if (run.Request.Operation == CatalogAnswerOperation.AuthorSeries)
            {
                // The saved projections carry works, not an author-to-series edge; deriving one here would be a second, unreviewed rule.
                run.Reports.Add(new CatalogSourceReport("saved-catalog", CatalogSourceState.Unsupported, CatalogAnswerCodes.OperationNotSupported, null));
                return Compose(run, CatalogAnswerOutcome.Unsupported, subject, reasons: [CatalogAnswerCodes.OperationNotSupported]);
            }

            return Evaluate(run, subject, await LoadSavedAuthorAsync(run, savedId, ct), null, ct);
        }

        return Evaluate(run, subject, await LoadAuthorAsync(run, subject, null, ct), null, ct);
    }

    private async Task<CatalogAnswer> TitleAsync(Run run, CancellationToken ct)
    {
        var title = run.Request.Subject is { Kind: CatalogSubjectKind.Title } named ? named.Label : run.Request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return Compose(run, CatalogAnswerOutcome.InsufficientEvidence, reasons: [CatalogAnswerCodes.SubjectRequired]);
        }

        var subject = CatalogSubjectRef.Title(title);
        var seed = await SeedAsync(run, title, run.Request.ProviderId, ct);
        if (seed.Failure is { } failure) return Compose(run, failure.Outcome, subject, reasons: failure.Reasons);
        var entries = seed.Candidates
            .Select(candidate => CatalogAnswerEntries.FromCandidate(candidate, seed.ObservedAtUtc))
            .Where(entry => entry is not null)
            .Cast<CatalogAnswerEntry>()
            .ToArray();
        var page = new Page(entries, [], seed.Exhausted, seed.More, false, null, [], "title-search-first-page", CatalogReadKind.TitleSearch, seed.ProviderId, seed.Gaps);
        return Evaluate(run, subject, page, null, ct);
    }

    // ========================================================================================== follow-ups from a root

    private async Task<CatalogAnswer> FollowUpAsync(Run run, Guid rootId, CancellationToken ct)
    {
        var request = run.Request;
        var lease = contexts.TryGetRoot(run.Caller, rootId, run.Now);
        var window = contexts.TryGetWindow(run.Caller, rootId, run.Now);
        if (lease is null || window is null)
        {
            // Another user's, another room's, expired, evicted or never retained all look the same, and nothing is refetched.
            return Compose(run, CatalogAnswerOutcome.ContextExpired, request.Subject, reasons: [CatalogAnswerCodes.ContextExpiredReason]);
        }

        run.Root = lease;
        if (request.Subject is { } named && (window.Subject is null || !SameSubject(named, window.Subject)))
        {
            // A different subject inside the same root: a new lookup that spends the same journey.
            return await FreshAsync(run, ct);
        }

        var subject = request.Subject ?? window.Subject;
        if (subject is null)
        {
            // The window holds only choices still to be made; they are shown again, with no call.
            var reasons = window.ScopeCode switch
            {
                "author-choices" => new[] { CatalogAnswerCodes.AuthorChoices },
                "subject-choices" => [CatalogAnswerCodes.AuthorChoices, CatalogAnswerCodes.SeriesChoices],
                _ => new[] { CatalogAnswerCodes.SeriesChoices }
            };
            return Compose(run, CatalogAnswerOutcome.Clarification, choices: window.Choices, reasons: reasons,
                coverage: CoverageOf(run, window));
        }

        if (!AppliesTo(request.Operation, subject.Kind))
        {
            // E.g. a position asked about an author, or an author's works asked about a series: nothing is read or reused.
            return Compose(run, CatalogAnswerOutcome.InsufficientEvidence, subject, reasons: [CatalogAnswerCodes.SubjectInvalid], coverage: CoverageOf(run, window));
        }

        // What the window read must be what the answer needs: an author's works page is no evidence about their series, and a
        // retained window is never read as absence for a different read.
        var needed = ReadKindFor(request.Operation, subject.Kind);
        if (request.ContinuationToken is { } token)
        {
            return window.ReadKind == needed
                ? await ContinueAsync(run, subject, window, token, ct)
                : Compose(run, CatalogAnswerOutcome.InsufficientEvidence, subject, reasons: [CatalogAnswerCodes.ContinuationUnavailable], coverage: CoverageOf(run, window));
        }

        if (window.ReadKind != needed)
        {
            // The authorized read is made now, on the same root, journey and admission (a fresh operation, never a fresh allowance).
            run.Request = request with { Subject = subject };
            return await FreshAsync(run, ct);
        }

        // Zero provider reads: the retained entries answer it, and a window that was cut says so instead of claiming it was read.
        var page = new Page(window.Entries, window.Series, window.Exhausted, window.MoreAvailable, window.Truncated, window.ContinuationToken, window.Sources, window.ScopeCode, window.ReadKind, window.ProviderId, window.SourceGaps);
        return Evaluate(run, subject, page, window, ct, retain: false);
    }

    private async Task<CatalogAnswer> ContinueAsync(Run run, CatalogSubjectRef subject, CatalogRetainedWindow window, string token, CancellationToken ct)
    {
        if (window.ContinuationToken is null || !string.Equals(window.ContinuationToken, token, StringComparison.Ordinal) || subject.Native is null)
        {
            // A token the retained window did not issue to this root is refused with no call and no charge.
            return Compose(run, CatalogAnswerOutcome.InsufficientEvidence, subject, reasons: [CatalogAnswerCodes.ContinuationUnavailable],
                coverage: CoverageOf(run, window));
        }

        Page page;
        switch (subject.Kind)
        {
            case CatalogSubjectKind.Series:
                page = await LoadSeriesMembersAsync(run, subject, token, ct);
                break;
            case CatalogSubjectKind.Author:
                page = await LoadAuthorAsync(run, subject, token, ct);
                break;
            default:
                return Compose(run, CatalogAnswerOutcome.InsufficientEvidence, subject, reasons: [CatalogAnswerCodes.ContinuationUnavailable]);
        }

        if (page.Failure is null)
        {
            // Pages accumulate: the answer is re-evaluated over everything retained plus this page, each record once.
            var known = window.Entries.Select(entry => entry.IdentityKey).ToHashSet(StringComparer.Ordinal);
            page = page with
            {
                Entries = [.. window.Entries, .. page.Entries.Where(entry => known.Add(entry.IdentityKey))],
                Series = [.. window.Series, .. page.Series]
            };
        }

        return Evaluate(run, subject, page, window, ct);
    }

    private static bool SameSubject(CatalogSubjectRef a, CatalogSubjectRef b) =>
        a.Kind == b.Kind && a.SavedId == b.SavedId && a.Native == b.Native;

    // ========================================================================================================== evaluate

    private CatalogAnswer Evaluate(Run run, CatalogSubjectRef subject, Page page, CatalogRetainedWindow? prior, CancellationToken ct, bool retain = true)
    {
        if (page.Failure is { } failure)
        {
            return Compose(run, failure.Outcome, subject, reasons: failure.Reasons, coverage: CoverageOf(run, page));
        }

        var request = run.Request;
        if (!AppliesTo(request.Operation, subject.Kind))
        {
            return Compose(run, CatalogAnswerOutcome.InsufficientEvidence, subject, reasons: [CatalogAnswerCodes.SubjectInvalid], coverage: CoverageOf(run, page));
        }

        // The last boundary before anything is retained or returned: past the deadline (or cancelled), nothing more is kept.
        ct.ThrowIfCancellationRequested();
        var today = DateOnly.FromDateTime(run.Now.UtcDateTime);
        var scope = new CatalogPolicyScope(page.Exhausted && !page.Truncated, page.ProviderId is null, page.SourceGaps);
        IReadOnlyList<CatalogAnswerSeries> series = page.Series;
        CatalogSelection selection;
        var anchor = request.Anchor;
        var byAuthor = subject.Kind == CatalogSubjectKind.Author;
        switch (request.Operation)
        {
            case CatalogAnswerOperation.LatestPublished:
                selection = byAuthor
                    ? CatalogAnswerPolicy.WithinAuthorScope(page.Entries, subject, scope, (entries, inner) => CatalogAnswerPolicy.Latest(entries, today, inner, request.Filter))
                    : CatalogAnswerPolicy.Latest(page.Entries, today, scope, request.Filter);
                break;
            case CatalogAnswerOperation.Position:
                selection = CatalogAnswerPolicy.Position(page.Entries, request.Position!.Value, request.Order, scope, request.Filter);
                break;
            case CatalogAnswerOperation.AfterAnchor:
                var key = anchor?.SavedId is { } savedAnchor ? CatalogAnswerEntry.KeyOf(savedAnchor) : anchor?.Native is { } nativeAnchor ? CatalogAnswerEntry.KeyOf(nativeAnchor) : null;
                selection = CatalogAnswerPolicy.AfterAnchor(page.Entries, key, key is null ? request.Position : null, request.Order, scope, request.Filter);
                break;
            case CatalogAnswerOperation.UpcomingReleases:
                selection = byAuthor
                    ? CatalogAnswerPolicy.WithinAuthorScope(page.Entries, subject, scope, (entries, inner) => CatalogAnswerPolicy.Upcoming(entries, today, false, inner, request.Filter))
                    : CatalogAnswerPolicy.Upcoming(page.Entries, today, false, scope, request.Filter);
                break;
            case CatalogAnswerOperation.NextRelease:
                selection = byAuthor
                    ? CatalogAnswerPolicy.WithinAuthorScope(page.Entries, subject, scope, (entries, inner) => CatalogAnswerPolicy.Upcoming(entries, today, true, inner, request.Filter))
                    : CatalogAnswerPolicy.Upcoming(page.Entries, today, true, scope, request.Filter);
                break;
            case CatalogAnswerOperation.PublicationForTitle:
                selection = CatalogAnswerPolicy.PublicationForTitle(page.Entries, subject.Label, today, scope);
                break;
            case CatalogAnswerOperation.SeriesMembers:
                selection = CatalogAnswerPolicy.Members(page.Entries, request.Order, scope, request.Filter);
                break;
            case CatalogAnswerOperation.AuthorWorks:
                selection = CatalogAnswerPolicy.AuthorWorks(page.Entries, subject, scope);
                break;
            default:
                selection = CatalogAnswerPolicy.AuthorSeries(page.Series, scope, out series);
                break;
        }

        var reasons = selection.Reasons.ToList();
        var qualifiers = selection.Qualifiers.ToList();
        CatalogAnswerContextRef? context = null;
        string? token = page.Token;
        if (retain)
        {
            var kept = RetainWindow(run, subject, page);
            if (kept is null)
            {
                reasons.Add(CatalogAnswerCodes.ContextNotRetained);
                token = null;
            }
            else
            {
                token = kept.ContinuationToken;
                // What was retained for follow-ups is smaller than what this answer was evaluated on: said, and no continuation offered.
                if (kept.Truncated && !page.Truncated) reasons.Add(CatalogAnswerCodes.ContextWindowCut);
                context = ContextFor(run, subject);
                if (context is null) reasons.Add(CatalogAnswerCodes.ContextNotRetained);
            }
        }
        else if (run.Root is { } root)
        {
            context = ContextFor(run, subject);
        }

        return Compose(
            run, selection.Outcome, subject, anchor, selection.Entries, series, [], selection.Groups, qualifiers, reasons,
            CoverageOf(run, page), token, selection.AvailableOrders, context);
    }

    private CatalogRetainedWindow? RetainWindow(Run run, CatalogSubjectRef? subject, Page page, IReadOnlyList<CatalogAnswerChoice>? choices = null)
    {
        if (!EnsureRoot(run)) return null;
        var window = new CatalogRetainedWindow(
            run.Request.Operation, subject, subject?.Order ?? run.Request.Order, page.Entries, page.Series, choices ?? [],
            page.Exhausted, page.More, page.Truncated, page.SourceGaps, page.Sources, page.ScopeCode, page.ProviderId, page.Token, run.Now, page.ReadKind);
        return contexts.Retain(run.Caller, run.Root!.RootId, window, run.Now);
    }

    private static CatalogAnswerContextRef? ContextFor(Run run, CatalogSubjectRef? subject)
    {
        if (run.Root is null) return null;
        var reference = new CatalogAnswerContextRef(run.Root.RootId, run.Request.Operation, subject, run.Request.Anchor, run.Request.Order);
        // A reference that does not fit its bound is not issued (it is never cut): the caller is told nothing was retained for follow-ups.
        return reference.ToJson() is null ? null : reference;
    }

    private bool EnsureRoot(Run run)
    {
        if (run.Root is not null) return true;
        run.Root = contexts.CreateRoot(run.Caller, run.Now);
        return run.Root is not null;
    }

    // ================================================================================================== saved evidence

    private async Task<Page> LoadSavedSeriesAsync(Run run, Guid seriesId, CatalogSubjectRef subject, CancellationToken ct)
    {
        var rows = await saved.GetSeriesEvidenceAsync(seriesId, 0, options.SavedPageSize + 1, ct);
        ct.ThrowIfCancellationRequested();
        var more = rows.Count > options.SavedPageSize;
        var entries = rows.Take(options.SavedPageSize)
            .Select(row => CatalogAnswerEntries.FromSaved(row.Work, CatalogAnswerEntries.SavedPositions(row), subject.Label))
            .ToArray();
        var report = new CatalogSourceReport("saved-catalog", CatalogSourceState.SavedCatalog, null, more ? DiscoveryCompletion.ItemTruncated : DiscoveryCompletion.ExhaustedInCheckedScope);
        run.Reports.Add(report);
        // The saved catalog is only what this library already knows, so it is never an exhaustive series list, and a cut page says so.
        return new Page(entries, [], !more, false, more, null, [report], "saved-series-members", CatalogReadKind.SeriesMembers, null, false);
    }

    private async Task<Page> LoadSavedAuthorAsync(Run run, Guid authorId, CancellationToken ct)
    {
        var rows = await saved.GetAuthorEvidenceAsync(authorId, 0, options.SavedPageSize + 1, ct);
        ct.ThrowIfCancellationRequested();
        var more = rows.Count > options.SavedPageSize;
        var entries = rows.Take(options.SavedPageSize).Select(row => CatalogAnswerEntries.FromSaved(row, [], null)).ToArray();
        var report = new CatalogSourceReport("saved-catalog", CatalogSourceState.SavedCatalog, null, more ? DiscoveryCompletion.ItemTruncated : DiscoveryCompletion.ExhaustedInCheckedScope);
        run.Reports.Add(report);
        return new Page(entries, [], !more, false, more, null, [report], "saved-author-works", CatalogReadKind.AuthorWorks, null, false);
    }

    // ================================================================================================ remote discovery

    private async Task<Page> LoadSeriesMembersAsync(Run run, CatalogSubjectRef subject, string? token, CancellationToken ct)
    {
        var native = subject.Native!;
        var prepared = await PrepareDiscoveryAsync(run, native.ProviderId, DiscoveryOperation.GetSeriesMembers, native, ct);
        if (prepared.Failure is not null) return FailedPage(prepared);

        DiscoveryOperationBudget operation;
        DiscoveryCursor? cursor = null;
        if (token is null)
        {
            operation = run.Journey!.BeginOperation(DiscoveryOperation.GetSeriesMembers, native.ProviderId, native)!;
            if (operation is null) return FailedPage(JourneyEnded(run, native.ProviderId));
        }
        else
        {
            var redeemed = run.Journey!.Redeem(token, run.Caller.OwnerId, DiscoveryOperation.GetSeriesMembers, native.ProviderId, native);
            if (redeemed.Status != ContinuationStatus.Accepted || redeemed.Operation is null || redeemed.Cursor is null)
            {
                return FailedPage(new Prepared(null, new Failure(CatalogAnswerOutcome.InsufficientEvidence, [CatalogAnswerCodes.ContinuationUnavailable]), null));
            }

            operation = redeemed.Operation;
            cursor = redeemed.Cursor;
        }

        run.ProviderReads++;
        var result = await prepared.Discovery!.GetSeriesMembersAsync(new SeriesRef(subject.Label, native), cursor, operation, ct);
        var entries = result.Items.Select(member => CatalogAnswerEntries.FromMember(member, subject.Label)).ToArray();
        return FromDiscovery(run, result, native.ProviderId, operation, "series-members-page", CatalogReadKind.SeriesMembers, entries, [], ct);
    }

    private async Task<Page> LoadAuthorAsync(Run run, CatalogSubjectRef subject, string? token, CancellationToken ct)
    {
        var native = subject.Native!;
        var kind = run.Request.Operation == CatalogAnswerOperation.AuthorSeries ? DiscoveryOperation.GetAuthorSeries : DiscoveryOperation.GetAuthorWorks;
        var prepared = await PrepareDiscoveryAsync(run, native.ProviderId, kind, native, ct);
        if (prepared.Failure is not null) return FailedPage(prepared);

        DiscoveryOperationBudget operation;
        DiscoveryCursor? cursor = null;
        if (token is null)
        {
            operation = run.Journey!.BeginOperation(kind, native.ProviderId, native)!;
            if (operation is null) return FailedPage(JourneyEnded(run, native.ProviderId));
        }
        else
        {
            var redeemed = run.Journey!.Redeem(token, run.Caller.OwnerId, kind, native.ProviderId, native);
            if (redeemed.Status != ContinuationStatus.Accepted || redeemed.Operation is null || redeemed.Cursor is null)
            {
                return FailedPage(new Prepared(null, new Failure(CatalogAnswerOutcome.InsufficientEvidence, [CatalogAnswerCodes.ContinuationUnavailable]), null));
            }

            operation = redeemed.Operation;
            cursor = redeemed.Cursor;
        }

        run.ProviderReads++;
        var author = new AuthorRef(subject.Label, native);
        if (kind == DiscoveryOperation.GetAuthorSeries)
        {
            var seriesResult = await prepared.Discovery!.GetAuthorSeriesAsync(author, cursor, operation, ct);
            var series = seriesResult.Items.Select(item => new CatalogAnswerSeries(
                item.Series.Label, item.Series.Id, null, item.Relationship, item.Series.IsCompleted, item.Series.PrimaryBooksCount,
                item.Series.Canonical is { State: not CanonicalLinkState.NotStated } link ? link.State.ToString() : null)).ToArray();
            return FromDiscovery(run, seriesResult, native.ProviderId, operation, "author-series-page", CatalogReadKind.AuthorSeries, [], series, ct);
        }

        var worksResult = await prepared.Discovery!.GetAuthorWorksAsync(author, cursor, operation, ct);
        var entries = worksResult.Items.Select(CatalogAnswerEntries.FromAuthorWork).ToArray();
        return FromDiscovery(run, worksResult, native.ProviderId, operation, "author-works-page", CatalogReadKind.AuthorWorks, entries, [], ct);
    }

    private static Page FromDiscovery<T>(
        Run run, DiscoveryResult<T> result, string providerId, DiscoveryOperationBudget operation, string scopeCode, CatalogReadKind readKind,
        CatalogAnswerEntry[] entries, CatalogAnswerSeries[] series, CancellationToken ct)
    {
        // The read has returned, so its physical use is recorded whatever happens next. Then the deadline is checked: a read that
        // ignored cancellation and finished late is not turned into a source report, a continuation or a retained window.
        run.Usages.Add(result.Usage);
        run.LastProviderId = providerId;
        ct.ThrowIfCancellationRequested();
        var (state, failure) = result.Outcome switch
        {
            DiscoveryOutcome.Ok or DiscoveryOutcome.Empty or DiscoveryOutcome.Ambiguous or DiscoveryOutcome.NoUsableItems => (CatalogSourceState.Checked, (CatalogAnswerOutcome?)null),
            DiscoveryOutcome.Partial => (CatalogSourceState.Failed, null),
            DiscoveryOutcome.Unsupported or DiscoveryOutcome.Unverified => (CatalogSourceState.Unsupported, CatalogAnswerOutcome.Unsupported),
            DiscoveryOutcome.Unavailable => (CatalogSourceState.Unavailable, CatalogAnswerOutcome.Unavailable),
            DiscoveryOutcome.TimedOut => (CatalogSourceState.TimedOut, CatalogAnswerOutcome.TimedOut),
            DiscoveryOutcome.Throttled => (CatalogSourceState.Throttled, CatalogAnswerOutcome.Throttled),
            _ => (CatalogSourceState.Failed, CatalogAnswerOutcome.Failed)
        };

        var gap = state is not CatalogSourceState.Checked;
        var items = entries.Length + series.Length;
        var report = new CatalogSourceReport(providerId, state, FirstNote(result.Notes), result.Completion);
        run.Reports.Add(report);
        var notes = result.Notes.ToList();
        if (result.Outcome == DiscoveryOutcome.NoUsableItems) notes.Add(CatalogAnswerCodes.SeedNoUsableRecords);

        // A failure that still returned items (timeout/throttle/partial) is a qualified partial, never an empty answer.
        if (failure is { } outcome && items == 0)
        {
            return new Page([], [], false, false, false, null, [report], scopeCode, readKind, providerId, true, new Failure(outcome, notes.Count == 0 ? [CatalogAnswerCodes.NoEntries] : notes));
        }

        string? issued = null;
        var more = result.Completion == DiscoveryCompletion.MoreAvailable;
        if (more && result.Next is not null)
        {
            var issue = run.Journey!.IssueContinuation(operation, result.Next);
            if (issue.Status == ContinuationIssueStatus.Issued) issued = issue.Token;
            else notes.Add(CatalogAnswerCodes.ContinuationUnavailable);
        }

        var truncated = result.Completion is DiscoveryCompletion.BudgetTruncated or DiscoveryCompletion.ItemTruncated
            or DiscoveryCompletion.ContextCapacity or DiscoveryCompletion.ProviderFailedPartial;
        return new Page(
            entries, series, result.Completion == DiscoveryCompletion.ExhaustedInCheckedScope && !gap, more, truncated || gap, issued,
            [report], scopeCode, readKind, providerId, gap, null, notes);
    }

    // =============================================================================================== seed search / choices

    private async Task<CatalogAnswer> ChoicesAsync(Run run, CatalogSubjectKind kind, string text, CancellationToken ct)
    {
        var choices = new List<CatalogAnswerChoice>();
        var normalized = CatalogText.NormalizeForMatch(text);
        var truncated = false;
        var reasons = new List<string>();

        // Saved ids first: the library's own authors/series whose normalized name equals the text. An association, never identity.
        if (normalized.Length > 0)
        {
            var found = kind == CatalogSubjectKind.Author
                ? await browse.FindAuthorsByNormalizedNameAsync(normalized, options.MaxChoices + 1, ct)
                : await browse.FindSeriesByNormalizedNameAsync(normalized, options.MaxChoices + 1, ct);
            foreach (var choice in found.Take(options.MaxChoices))
            {
                choices.Add(new CatalogAnswerChoice(
                    CatalogSubjectRef.Saved(kind, choice.Name, choice.Id), (choice.SampleTitles is [var sample, ..] ? sample : null), choice.KnownBookCount, null, "saved-catalog"));
            }

            truncated |= found.Count > options.MaxChoices;
            if (found.Count > 0) run.Reports.Add(new CatalogSourceReport("saved-catalog", CatalogSourceState.SavedCatalog, null, null));
        }

        if (kind == CatalogSubjectKind.Author) reasons.Add(CatalogAnswerCodes.AuthorChoices);
        else reasons.Add(CatalogAnswerCodes.SeriesChoices);

        var scopeCode = kind == CatalogSubjectKind.Author ? "author-choices" : "series-choices";
        var seed = await SeedAsync(run, text, run.Request.ProviderId, ct, allowWithoutSource: choices.Count > 0);
        var gaps = seed.Gaps;
        if (seed.Failure is null)
        {
            var remote = kind == CatalogSubjectKind.Author
                ? AuthorChoicesFrom(run, seed, text)
                : await SeriesChoicesFromAsync(run, seed, text, ct);
            var offered = remote.Choices;

            if (kind == CatalogSubjectKind.Series && AppliesTo(run.Request.Operation, CatalogSubjectKind.Author))
            {
                // A publication question naming an author ("the latest Tom Clancy book") is not a question about one of that
                // author's series. The seed read that produced the series claims carries the credits too, so the author is offered
                // first among the choices — still a choice: a name never selects, and nothing dependent has been read.
                var authors = AuthorChoicesFrom(run, seed, text, report: false).Choices;
                if (authors.Count > 0)
                {
                    offered = [.. authors, .. remote.Choices];
                    scopeCode = remote.Choices.Count > 0 ? "subject-choices" : "author-choices";
                    reasons = remote.Choices.Count > 0
                        ? [CatalogAnswerCodes.AuthorChoices, CatalogAnswerCodes.SeriesChoices]
                        : [CatalogAnswerCodes.AuthorChoices];
                }
            }

            foreach (var choice in offered)
            {
                if (choices.Count >= options.MaxChoices) { truncated = true; break; }
                choices.Add(choice);
            }

            gaps |= remote.Gaps;
            if (remote.Failure is not null && choices.Count == 0) return Compose(run, remote.Failure.Outcome, reasons: remote.Failure.Reasons);
        }
        else if (choices.Count == 0)
        {
            return Compose(run, seed.Failure.Outcome, reasons: seed.Failure.Reasons);
        }
        else
        {
            gaps = true;
        }

        var coverage = new CatalogAnswerCoverage([.. run.Reports], scopeCode, false, seed.More, truncated, run.Now);
        if (choices.Count == 0)
        {
            return Compose(run, CatalogAnswerOutcome.NoVerifiedMatch, reasons: [CatalogAnswerCodes.NoChoicesFound],
                qualifiers: [CatalogAnswerCodes.IncompleteCoverage], coverage: coverage);
        }

        // A name never selects: even one choice is offered for the user to confirm. Nothing dependent was fetched.
        var page = new Page([], [], false, seed.More, truncated, null, [.. run.Reports], scopeCode, CatalogReadKind.Choices, seed.ProviderId, gaps);
        ct.ThrowIfCancellationRequested();
        var kept = RetainWindow(run, null, page, choices);
        var qualifiers = new List<string>();
        if (truncated) qualifiers.Add(CatalogAnswerCodes.ListNotExhaustive);
        if (gaps) qualifiers.Add(CatalogAnswerCodes.SourceGaps);
        CatalogAnswerContextRef? context = null;
        if (kept is null) reasons.Add(CatalogAnswerCodes.ContextNotRetained);
        else context = ContextFor(run, null);
        return Compose(run, CatalogAnswerOutcome.Clarification, choices: choices, qualifiers: qualifiers, reasons: reasons, coverage: coverage, context: context);
    }

    private sealed record ChoiceSet(IReadOnlyList<CatalogAnswerChoice> Choices, bool Gaps, Failure? Failure);

    private static ChoiceSet AuthorChoicesFrom(Run run, SeedResult seed, string text, bool report = true)
    {
        // No source was asked (none eligible, with saved choices still shown): there is nothing to read ids from.
        if (seed.ProviderId is null) return new ChoiceSet([], true, null);
        var key = CatalogIdentityPolicy.AuthorKey(text);
        var found = new Dictionary<string, (CatalogAnswerChoice Choice, int Count)>(StringComparer.Ordinal);
        var sawAnyId = false;
        foreach (var candidate in seed.Candidates)
        {
            foreach (var credit in candidate.AuthorCredits)
            {
                if (!SourceRef.TryCreate(candidate.ProviderId, credit.SourceAuthorId, ExternalReferenceEntityType.Author, out var id)) continue;
                sawAnyId = true;
                // Only an exact normalized name; the id comes from the source's own credit, never from the name.
                if (!string.Equals(CatalogIdentityPolicy.AuthorKey(credit.Name), key, StringComparison.Ordinal)) continue;
                var identity = CatalogAnswerEntry.KeyOf(id!);
                if (found.TryGetValue(identity, out var existing))
                {
                    found[identity] = (existing.Choice, existing.Count + 1);
                    continue;
                }

                found[identity] = (new CatalogAnswerChoice(
                    CatalogSubjectRef.FromSource(CatalogSubjectKind.Author, credit.Name, id!), candidate.Title, 1, candidate.ProviderId, "credit-on-seed-record"), 1);
            }
        }

        var choices = found.Values.Select(item => item.Choice with { KnownCount = item.Count }).ToArray();
        if (choices.Length == 0 && !sawAnyId)
        {
            if (report)
            {
                run.Reports.Add(new CatalogSourceReport(seed.ProviderId ?? "answer", CatalogSourceState.Unsupported, CatalogAnswerCodes.AuthorIdsUnavailable, null));
            }

            return new ChoiceSet([], true, null);
        }

        return new ChoiceSet(choices, false, null);
    }

    private async Task<ChoiceSet> SeriesChoicesFromAsync(Run run, SeedResult seed, string text, CancellationToken ct)
    {
        var key = CatalogIdentityPolicy.TitleKey(text);
        var found = new Dictionary<string, CatalogAnswerChoice>(StringComparer.Ordinal);

        // First, the series the seed records themselves state (their own book-to-series claims, with the source's own series ids).
        foreach (var candidate in seed.Candidates)
        {
            foreach (var series in candidate.Series)
            {
                if (!SourceRef.TryCreate(candidate.ProviderId, series.SourceSeriesId, ExternalReferenceEntityType.Series, out var id) || string.IsNullOrWhiteSpace(series.Name)) continue;
                found.TryAdd(CatalogAnswerEntry.KeyOf(id!), new CatalogAnswerChoice(
                    CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, series.Name, id!, SeriesOrdering.Of(series)),
                    candidate.Title, null, candidate.ProviderId, "series-claim-on-seed-record"));
            }
        }

        // Otherwise ONE book-to-series read on the first usable anchor, under the same journey: never a fan-out over every hit.
        if (found.Count == 0 && seed.ProviderId is { } providerId)
        {
            var anchor = seed.Candidates
                .Select(candidate => CatalogAnswerEntries.FromCandidate(candidate, seed.ObservedAtUtc))
                .FirstOrDefault(entry => entry?.Source is not null);
            var active = await providers.FindActiveProviderAsync(providerId, ct);
            if (anchor?.Source is { } source && active is IBookMetadataDiscoveryProvider discovery &&
                discovery.Capabilities.Get(DiscoveryOperation.FindSeriesForBook).State != CapabilityState.Unsupported)
            {
                var prepared = await PrepareDiscoveryAsync(run, providerId, DiscoveryOperation.FindSeriesForBook, source, ct);
                if (prepared.Failure is not null) return new ChoiceSet([], true, prepared.Failure);
                var operation = run.Journey!.BeginOperation(DiscoveryOperation.FindSeriesForBook, providerId, source);
                if (operation is null) return new ChoiceSet([], true, JourneyEnded(run, providerId).Failure);
                run.ProviderReads++;
                var result = await discovery.FindSeriesClaimsForBookAsync(source, operation, ct);
                run.Usages.Add(result.Usage);
                ct.ThrowIfCancellationRequested();
                var failed = result.Outcome is not (DiscoveryOutcome.Ok or DiscoveryOutcome.Empty or DiscoveryOutcome.Partial or DiscoveryOutcome.NoUsableItems);
                run.Reports.Add(new CatalogSourceReport(providerId, failed ? StateOf(result.Outcome) : CatalogSourceState.Checked, FirstNote(result.Notes), result.Completion));
                if (failed && result.Items.Count == 0) return new ChoiceSet([], true, new Failure(OutcomeOf(result.Outcome), result.Notes));
                foreach (var claim in result.Items)
                {
                    if (claim.Series.Id is null) continue;
                    found.TryAdd(CatalogAnswerEntry.KeyOf(claim.Series.Id), new CatalogAnswerChoice(
                        CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, string.IsNullOrWhiteSpace(claim.Series.Label) ? claim.Series.Id.ExternalId : claim.Series.Label, claim.Series.Id, claim.Position?.Ordering ?? SeriesOrderingKind.Unspecified),
                        anchor.Title, claim.Series.PrimaryBooksCount, providerId, "series-claim-on-anchor-book"));
                }
            }
            else
            {
                run.Reports.Add(new CatalogSourceReport(providerId, CatalogSourceState.Unsupported, CatalogAnswerCodes.SeriesClaimsUnsupported, null));
                return new ChoiceSet([], true, null);
            }
        }

        // The ones whose label matches the typed text come first; the rest remain choices, never answers.
        var ordered = found.Values
            .OrderBy(choice => CatalogIdentityPolicy.TitleKey(choice.Reference.Label) == key ? 0 : 1)
            .ThenBy(choice => CatalogIdentityPolicy.TitleKey(choice.Reference.Label), StringComparer.Ordinal)
            .ThenBy(choice => choice.Reference.Native!.ExternalId, StringComparer.Ordinal)
            .ToArray();
        return new ChoiceSet(ordered, false, null);
    }

    private sealed record SeedResult(
        IReadOnlyList<BookCandidate> Candidates, DateTimeOffset ObservedAtUtc, string? ProviderId, bool Exhausted, bool More, bool Gaps, Failure? Failure);

    /// <summary>One budgeted ordinary search on one eligible source. Another source is an explicit later choice, never a silent fan-out.</summary>
    private async Task<SeedResult> SeedAsync(Run run, string text, string? preferredProviderId, CancellationToken ct, bool allowWithoutSource = false)
    {
        var active = await providers.GetActiveProvidersAsync(ct);
        var eligible = active.Where(provider => provider is IBudgetedBookMetadataProvider).ToArray();
        IBookMetadataProvider? chosen = preferredProviderId is null
            ? eligible.FirstOrDefault()
            : eligible.FirstOrDefault(provider => string.Equals(provider.Id, preferredProviderId, StringComparison.Ordinal));
        foreach (var other in active.Where(provider => !ReferenceEquals(provider, chosen)))
        {
            run.Reports.Add(new CatalogSourceReport(other.Id, CatalogSourceState.NotQueried, null, null));
        }

        if (chosen is null)
        {
            if (preferredProviderId is not null && active.Any(provider => string.Equals(provider.Id, preferredProviderId, StringComparison.Ordinal)))
            {
                // Active, but it offers no budgeted read: unsupported for this lookup, not switched off.
                run.Reports.Add(new CatalogSourceReport(preferredProviderId, CatalogSourceState.Unsupported, CatalogAnswerCodes.NoEligibleSource, null));
                return Failed(CatalogAnswerOutcome.Unsupported, CatalogAnswerCodes.NoEligibleSource);
            }

            if (preferredProviderId is not null)
            {
                var reason = providers.IsRegistered(preferredProviderId) ? "source-disabled" : "source-removed";
                run.Reports.Add(new CatalogSourceReport(preferredProviderId, CatalogSourceState.Unavailable, reason, null));
                return Failed(CatalogAnswerOutcome.Unavailable, reason);
            }

            run.Reports.Add(new CatalogSourceReport("answer", CatalogSourceState.Unsupported, CatalogAnswerCodes.NoEligibleSource, null));
            return allowWithoutSource ? new SeedResult([], run.Now, null, false, false, true, null) : Failed(CatalogAnswerOutcome.Unsupported, CatalogAnswerCodes.NoEligibleSource);
        }

        var budgeted = (IBudgetedBookMetadataProvider)chosen;
        var entered = Enter(run, chosen.Id);
        if (entered is not null) return new SeedResult([], run.Now, chosen.Id, false, false, true, entered);

        var operation = run.Journey!.BeginOperation(DiscoveryOperation.SearchBooks, chosen.Id, null);
        if (operation is null) return new SeedResult([], run.Now, chosen.Id, false, false, true, JourneyEnded(run, chosen.Id).Failure);
        run.ProviderReads++;
        run.LastProviderId = chosen.Id;
        var read = await budgeted.SearchBudgetedAsync(new BookSearchQuery(text), operation, ct);
        run.Usages.Add(read.Usage);
        ct.ThrowIfCancellationRequested();
        var (state, failure) = read.Outcome switch
        {
            DiscoveryOutcome.Ok or DiscoveryOutcome.Empty => (CatalogSourceState.Checked, (CatalogAnswerOutcome?)null),
            _ => (StateOf(read.Outcome), OutcomeOf(read.Outcome))
        };
        run.Reports.Add(new CatalogSourceReport(chosen.Id, state, FirstNote(read.Notes), null));
        if (failure is { } outcome)
        {
            return new SeedResult([], read.ObservedAtUtc, chosen.Id, false, false, true, new Failure(outcome, read.Notes));
        }

        var page = read.Value;
        // A search result is a first page: a sample, so it is never exhausted and an empty page is "nothing in this page".
        return new SeedResult(page?.Candidates ?? [], read.ObservedAtUtc, chosen.Id, false, page?.HasMore ?? false, false, null);
    }

    private static SeedResult Failed(CatalogAnswerOutcome outcome, string reason) =>
        new([], DateTimeOffset.MinValue, null, false, false, true, new Failure(outcome, [reason]));

    // ======================================================================================== admission and preparation

    private sealed record Failure(CatalogAnswerOutcome Outcome, IReadOnlyList<string> Reasons, TimeSpan RetryAfter = default);

    private sealed record Prepared(IBookMetadataDiscoveryProvider? Discovery, Failure? Failure, string? ProviderId);

    private async Task<Prepared> PrepareDiscoveryAsync(Run run, string providerId, DiscoveryOperation operation, SourceRef subject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var active = await providers.FindActiveProviderAsync(providerId, ct);
        if (active is null)
        {
            var reason = providers.IsRegistered(providerId) ? "source-disabled" : "source-removed";
            run.Reports.Add(new CatalogSourceReport(providerId, CatalogSourceState.Unavailable, reason, null));
            return new Prepared(null, new Failure(CatalogAnswerOutcome.Unavailable, [reason]), providerId);
        }

        if (active is not IBookMetadataDiscoveryProvider discovery)
        {
            run.Reports.Add(new CatalogSourceReport(providerId, CatalogSourceState.Unsupported, "not-implemented", null));
            return new Prepared(null, new Failure(CatalogAnswerOutcome.Unsupported, ["not-implemented"]), providerId);
        }

        var capability = discovery.Capabilities.Get(operation);
        if (capability.State == CapabilityState.Unsupported)
        {
            run.Reports.Add(new CatalogSourceReport(providerId, CatalogSourceState.Unsupported, capability.ReasonCode, null));
            return new Prepared(null, new Failure(CatalogAnswerOutcome.Unsupported, [capability.ReasonCode]), providerId);
        }

        // Resolving the source awaited a settings read, so time has passed: the root and the journey are checked again before any spend.
        var entered = Enter(run, providerId);
        return entered is null ? new Prepared(discovery, null, providerId) : new Prepared(null, entered, providerId);
    }

    /// <summary>The root, journey and per-user admission for this lookup, checked against a fresh instant. Null when the lookup may proceed.</summary>
    private Failure? Enter(Run run, string providerId)
    {
        run.Now = clock.UtcNow;
        if (run.Root is not null && contexts.TryGetRoot(run.Caller, run.Root.RootId, run.Now) is null)
        {
            return new Failure(CatalogAnswerOutcome.ContextExpired, [CatalogAnswerCodes.ContextExpiredReason]);
        }

        if (!EnsureRoot(run)) return new Failure(CatalogAnswerOutcome.Failed, [CatalogAnswerCodes.ContextNotRetained]);

        run.Journey ??= journeys.GetOrCreateForRoot(run.Caller.OwnerId, run.Root!.RootId, run.Root.ExpiresAtUtc, run.Now, timeProvider);
        if (run.Journey is null) return new Failure(CatalogAnswerOutcome.ContextExpired, [CatalogAnswerCodes.ContextExpiredReason]);

        if (run.Permit is null)
        {
            var admission = limiter.TryStart(run.Caller.OwnerId, run.Now, out var permit, out var retryAfter);
            if (admission != ExpansionAdmission.Admitted)
            {
                run.Reports.Add(new CatalogSourceReport(providerId, CatalogSourceState.NotQueried, CatalogAnswerCodes.AdmissionRefused, null));
                run.RetryAfter = retryAfter;
                return new Failure(
                    admission == ExpansionAdmission.Busy ? CatalogAnswerOutcome.Refused : CatalogAnswerOutcome.Throttled,
                    [CatalogAnswerCodes.AdmissionRefused], retryAfter);
            }

            run.Permit = permit;
        }

        return null;
    }

    private static Prepared JourneyEnded(Run run, string providerId)
    {
        run.Reports.Add(new CatalogSourceReport(providerId, CatalogSourceState.NotQueried, DiscoveryCodes.JourneyExpired, null));
        return new Prepared(null, new Failure(CatalogAnswerOutcome.ContextExpired, [DiscoveryCodes.JourneyExpired]), providerId);
    }

    private static string? FirstNote(IReadOnlyList<string> notes) => notes.Count > 0 ? notes[0] : null;

    private static CatalogSourceState StateOf(DiscoveryOutcome outcome) => outcome switch
    {
        DiscoveryOutcome.Unsupported or DiscoveryOutcome.Unverified => CatalogSourceState.Unsupported,
        DiscoveryOutcome.Unavailable => CatalogSourceState.Unavailable,
        DiscoveryOutcome.TimedOut => CatalogSourceState.TimedOut,
        DiscoveryOutcome.Throttled => CatalogSourceState.Throttled,
        _ => CatalogSourceState.Failed
    };

    private static CatalogAnswerOutcome OutcomeOf(DiscoveryOutcome outcome) => outcome switch
    {
        DiscoveryOutcome.Unsupported or DiscoveryOutcome.Unverified => CatalogAnswerOutcome.Unsupported,
        DiscoveryOutcome.Unavailable => CatalogAnswerOutcome.Unavailable,
        DiscoveryOutcome.TimedOut => CatalogAnswerOutcome.TimedOut,
        DiscoveryOutcome.Throttled => CatalogAnswerOutcome.Throttled,
        _ => CatalogAnswerOutcome.Failed
    };

    private static Page FailedPage(Prepared prepared) =>
        new([], [], false, false, false, null, [], "none", CatalogReadKind.Choices, prepared.ProviderId, true, prepared.Failure);

    // ===================================================================================================== composition

    private sealed record Page(
        IReadOnlyList<CatalogAnswerEntry> Entries,
        IReadOnlyList<CatalogAnswerSeries> Series,
        bool Exhausted,
        bool More,
        bool Truncated,
        string? Token,
        IReadOnlyList<CatalogSourceReport> Sources,
        string ScopeCode,
        CatalogReadKind ReadKind,
        string? ProviderId,
        bool SourceGaps,
        Failure? Failure = null,
        IReadOnlyList<string>? Notes = null);

    private static CatalogAnswerCoverage CoverageOf(Run run, Page page) =>
        new([.. run.Reports.Count > 0 ? run.Reports : page.Sources], page.ScopeCode, page.Exhausted && !page.Truncated, page.More, page.Truncated, run.Now);

    private static CatalogAnswerCoverage CoverageOf(Run run, CatalogRetainedWindow window) =>
        new(window.Sources, window.ScopeCode, window.Exhausted && !window.Truncated, window.MoreAvailable, window.Truncated, run.Now);

    private CatalogAnswer Compose(
        Run run,
        CatalogAnswerOutcome outcome,
        CatalogSubjectRef? subject = null,
        CatalogSubjectRef? anchor = null,
        IReadOnlyList<CatalogAnswerEntry>? entries = null,
        IReadOnlyList<CatalogAnswerSeries>? series = null,
        IReadOnlyList<CatalogAnswerChoice>? choices = null,
        IReadOnlyList<CatalogAnswerGroup>? groups = null,
        IReadOnlyList<string>? qualifiers = null,
        IReadOnlyList<string>? reasons = null,
        CatalogAnswerCoverage? coverage = null,
        string? token = null,
        IReadOnlyList<SeriesOrderingKind>? orders = null,
        CatalogAnswerContextRef? context = null)
    {
        var usage = new CatalogAnswerUsage(
            run.ProviderReads,
            run.Usages.Sum(item => item.Attempts),
            run.Usages.Sum(item => item.Retries),
            run.Usages.Sum(item => item.Pages),
            run.Usages.Sum(item => item.Items),
            timeProvider.GetElapsedTime(run.Started));
        var retryAfter = TimeSpan.Zero;
        if (outcome is CatalogAnswerOutcome.Refused or CatalogAnswerOutcome.Throttled) retryAfter = run.RetryAfter;
        return new CatalogAnswer
        {
            Operation = run.Request.Operation,
            Outcome = outcome,
            Subject = subject,
            Anchor = anchor,
            Order = subject?.Order is { } order && order != SeriesOrderingKind.Unspecified ? order : run.Request.Order,
            Position = run.Request.Position,
            Entries = entries ?? [],
            Series = series ?? [],
            Choices = choices ?? [],
            Groups = groups ?? [],
            Qualifiers = qualifiers ?? [],
            Reasons = reasons ?? [],
            AvailableOrders = orders ?? [],
            Coverage = coverage ?? new CatalogAnswerCoverage([.. run.Reports], "none", false, false, false, run.Now),
            Usage = usage,
            Today = DateOnly.FromDateTime(run.Now.UtcDateTime),
            Context = context,
            ContinuationToken = token,
            RetryAfter = retryAfter
        };
    }

    private sealed class Run(CatalogAnswerCaller caller, CatalogAnswerRequest request, DateTimeOffset now, long started)
    {
        public CatalogAnswerCaller Caller { get; } = caller;

        public CatalogAnswerRequest Request { get; set; } = request;

        public DateTimeOffset Now { get; set; } = now;

        public long Started { get; } = started;

        public CatalogAnswerRootLease? Root { get; set; }

        public DiscoveryJourney? Journey { get; set; }

        public IDisposable? Permit { get; set; }

        public TimeSpan RetryAfter { get; set; }

        public int ProviderReads { get; set; }

        public string? LastProviderId { get; set; }

        public List<CatalogSourceReport> Reports { get; } = [];

        public List<DiscoveryUsage> Usages { get; } = [];
    }
}
