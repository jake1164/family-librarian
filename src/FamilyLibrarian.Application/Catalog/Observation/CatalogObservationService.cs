using System.Globalization;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;

namespace FamilyLibrarian.Application.Catalog.Observation;

/// <summary>
/// The single path by which a provider's record changes durable catalog identity or evidence (besides lock-coordinated
/// retirement). Three intents are deliberately distinct: an ordinary <b>Resolve</b>, an administrator <b>Refresh</b> of a stored
/// reference, and an administrator <b>Discover</b> of other sources. Every write runs inside the catalog write scope: one fresh
/// attempt, one transaction, one cross-process lock, at most one clean retry. Provider calls stay outside it and are never
/// repeated by a retry.
/// </summary>
public sealed class CatalogObservationService(
    IActiveMetadataProviderResolver providers,
    ICatalogRepository reader,
    ICatalogWriteScope writeScope,
    NotificationService notifications,
    IClock clock,
    ICatalogResolutionDiagnostics? diagnostics = null,
    TimeSpan? refreshDeadline = null)
{
    /// <summary>The whole administrator refresh (every provider, refresh and discovery) must finish within this by default.</summary>
    public static readonly TimeSpan DefaultRefreshDeadline = TimeSpan.FromSeconds(60);

    /// <summary>The most providers one refresh searches for other sources.</summary>
    internal const int MaximumDiscoveryProviders = 6;

    /// <summary>The most detail fetches one discovery makes per provider.</summary>
    internal const int MaximumDiscoveryDetails = 3;

    private const string ObservedOutcome = "Observed";

    // ------------------------------------------------------------------------------------------------------------ resolve

    /// <summary>
    /// Resolves a provider record to a Work. A saved reference returns its Work with no provider call and no write. Otherwise only
    /// the selected, active provider is asked, once; its record is matched by validated ISBN proof, attached to the one Work it
    /// proves, or becomes a separate Work with the evidence that stopped an attachment kept. Cross-source discovery never happens here.
    /// </summary>
    /// <exception cref="CatalogResolutionException">
    /// An expected, typed failure. For every fault except <see cref="CatalogResolutionFault.OutcomeUnknown"/> nothing was written.
    /// <c>OutcomeUnknown</c> means the commit's outcome could not be established, so the change may well exist.
    /// </exception>
    public async Task<CatalogWorkResolution> ResolveAsync(string providerId, string externalId, CancellationToken cancellationToken)
    {
        ValidateProviderReference(providerId, externalId);

        // A saved reference never depends on the source still being enabled or reachable, and it is never re-validated here.
        var saved = await reader.FindWorkByExternalReferenceAsync(providerId, externalId, cancellationToken);
        if (saved is not null)
        {
            return new CatalogWorkResolution(saved, false);
        }

        var provider = await providers.FindActiveProviderAsync(providerId, cancellationToken);
        if (provider is null)
        {
            throw providers.IsRegistered(providerId)
                ? new CatalogResolutionException(CatalogResolutionFault.ProviderNotEnabled, $"The metadata provider '{providerId}' is not enabled.")
                : new CatalogResolutionException(CatalogResolutionFault.UnknownProvider, $"The metadata provider '{providerId}' is not installed.");
        }

        var candidate = await provider.GetDetailsAsync(externalId, cancellationToken)
            ?? throw new CatalogResolutionException(
                CatalogResolutionFault.CandidateNotFound, $"The metadata candidate '{providerId}/{externalId}' was not found.");

        var write = await writeScope.ExecuteAsync(
            (session, token) => ResolveAttemptAsync(session, provider.Id, candidate, token),
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
            throw new CatalogResolutionException(fault, "The candidate's ISBN is contested by a concurrent change, so it was not attached.");
        }

        var recovered = write.Status == CatalogWriteStatus.Recovered;
        var resolution = new CatalogWorkResolution(attempt.Work!, attempt.WasCreated && !recovered)
        {
            Outcome = recovered ? CatalogResolutionOutcome.Existing : attempt.Kind switch
            {
                ObservationKind.Created => CatalogResolutionOutcome.Created,
                ObservationKind.Attached => CatalogResolutionOutcome.Attached,
                _ => CatalogResolutionOutcome.Existing
            },
            Diagnostics = recovered ? CatalogResolutionDiagnostics.None : attempt.Diagnostics,
            Detail = recovered || attempt.Detail is null ? null : attempt.Detail
        };

        if (write.Status == CatalogWriteStatus.Committed && attempt.WasCreated)
        {
            // Only after the catalog write actually commits, and only for a Work that was just created: attach, refresh, replay and
            // recovery never notify. A recovered creation may lose its notice (the accepted at-most-once limit); it never repeats one.
            diagnostics?.Report(attempt.Work!.Id, attempt.Diagnostics);
            await NotifyFollowersAsync(attempt.Work!, attempt, cancellationToken);
        }

        return resolution;
    }

    private async Task<ObservationAttempt> ResolveAttemptAsync(
        ICatalogWriteSession session,
        string providerId,
        BookCandidate candidate,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;

        // Every row written for this observation carries the id the provider was asked under, so the reference, snapshot and claims
        // can never disagree about which source they came from.
        candidate = candidate with { ProviderId = providerId };

        // Another writer may have created this reference while this one waited for the lock: nothing to validate, nothing to write.
        var createdMeanwhile = await catalog.FindWorkByExternalReferenceAsync(providerId, candidate.ExternalId, cancellationToken);
        if (createdMeanwhile is not null)
        {
            return ObservationAttempt.Found(createdMeanwhile);
        }

        // Candidate-intrinsic pass first: only compatible editions may enumerate possible Works.
        var intrinsic = CatalogEditionProjection.Project(candidate, null);
        var owners = await catalog.MapIsbnOwnersAsync(AllIsbns(candidate), cancellationToken);
        var proof = CatalogProof.Assess(intrinsic, owners);

        switch (proof.Kind)
        {
            case ProofKind.MultipleWorks:
                return await CreateWorkAsync(session, providerId, candidate, owners, new ObservationDetail(ObservationKind.Collision, "MultipleWorks"), cancellationToken);
            case ProofKind.ExcludedEditionOverlapOnly:
                return await CreateWorkAsync(session, providerId, candidate, owners, new ObservationDetail(ObservationKind.NoProof, "ExcludedEditionOverlap"), cancellationToken);
            case ProofKind.SingleWork:
                var target = await catalog.GetWorkAsync(proof.TargetWorkId!.Value, cancellationToken);
                if (target is null || target.IsRetired)
                {
                    break;
                }

                if (session.RetryCause == CatalogWriteRetryCause.UniqueViolation)
                {
                    // The previous attempt lost an ISBN race to a writer outside the lock. That the ISBN now has an owner is not
                    // proof the owner is this book, so the contested ISBN is not adopted.
                    return ObservationAttempt.Failed(CatalogResolutionFault.Conflict);
                }

                var verdict = CatalogIdentityPolicy.EvaluateRecord(candidate, IdentityOf(target), intrinsic.Compatible);
                if (!verdict.IsCompatible)
                {
                    return await CreateWorkAsync(
                        session, providerId, candidate, owners, new ObservationDetail(ObservationKind.Collision, verdict.Rejection!.Value.ToString()), cancellationToken);
                }

                var targeted = ProjectAgainst(candidate, target, owners);
                if (!targeted.ProofIsbns.Any(isbn => owners.TryGetValue(isbn, out var owner) && owner == target.Id))
                {
                    // The edition that carried the proof is itself ruled out for this Work (for example its language): no proof.
                    return await CreateWorkAsync(
                        session, providerId, candidate, owners, new ObservationDetail(ObservationKind.NoProof, "ExcludedEditionOverlap"), cancellationToken);
                }

                return await AttachAsync(session, target, providerId, candidate, targeted, EditionIdentityBasis.Isbn, ObservationKind.Attached, cancellationToken);
        }

        return await CreateWorkAsync(session, providerId, candidate, owners, null, cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------------------ create

    private async Task<ObservationAttempt> CreateWorkAsync(
        ICatalogWriteSession session,
        string providerId,
        BookCandidate candidate,
        IReadOnlyDictionary<string, Guid> owners,
        ObservationDetail? detail,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;
        var now = clock.UtcNow;
        var recordClass = CatalogVersionClass.ForRecord(candidate);

        // A contested ISBN stays only in the snapshot: the global ISBN uniqueness is never what arbitrates a creation.
        var projection = CatalogEditionProjection.Project(
            candidate, ProjectionTarget.ForCreation(recordClass, owners.Keys.ToHashSet(StringComparer.Ordinal)));

        var work = new Work(candidate.Title, null, null, null, PublicationStatus.Unknown, now);
        work.SetVersionClass(recordClass);

        var authorNotices = new List<AuthorNotice>();
        var seriesNotices = new List<SeriesNotice>();
        var ambiguousAuthors = new List<AmbiguousAuthorCredit>();
        var omittedSeries = new List<OmittedSeriesMembership>();

        await AddAuthorsAsync(session, work, candidate, providerId, now, authorNotices, ambiguousAuthors, cancellationToken);
        AddEditions(catalog, work, projection, providerId, EditionIdentityBasis.Created, now, explicitAdd: false);
        var series = await BindSeriesAsync(session, work, candidate, providerId, now, creating: true, seriesNotices, omittedSeries, cancellationToken);

        // The whole graph is added once, so every part is Added (a client-assigned key must never be left to discovery).
        catalog.AddWork(work);
        catalog.AddExternalReference(new ExternalReference(providerId, ExternalReferenceEntityType.Work, work.Id, candidate.ExternalId, now));

        var ledger = new ClaimLedger(catalog, work, providerId, now);
        var evidence = await RecordEvidenceAsync(
            session, work, candidate, projection, series, ledger, providerId, detail?.Kind.ToString() ?? ObservedOutcome, detail?.Reason,
            recordClass, creating: true, now, cancellationToken);

        return ObservationAttempt.Created(
            work,
            providerId,
            candidate.ExternalId,
            evidence,
            authorNotices,
            seriesNotices,
            new CatalogResolutionDiagnostics(ambiguousAuthors, omittedSeries),
            detail);
    }

    // ------------------------------------------------------------------------------------------------------------ attach

    /// <summary>
    /// Adds a provider record to a Work its ISBN proof names: the reference, the compatible editions, new evidence. Authors are
    /// never added to an existing Work. Used by Resolve (attach) and Discover; a known-reference Refresh has its own validation.
    /// </summary>
    private async Task<ObservationAttempt> AttachAsync(
        ICatalogWriteSession session,
        Work work,
        string providerId,
        BookCandidate candidate,
        EditionProjection projection,
        EditionIdentityBasis basis,
        ObservationKind kind,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;
        var now = clock.UtcNow;
        catalog.AddExternalReference(new ExternalReference(providerId, ExternalReferenceEntityType.Work, work.Id, candidate.ExternalId, now));

        var ledger = new ClaimLedger(catalog, work, providerId, now);
        var editionsAdded = AddEditions(catalog, work, projection, providerId, basis, now, explicitAdd: true);
        var seriesNotices = new List<SeriesNotice>();
        var omitted = new List<OmittedSeriesMembership>();
        var series = await BindSeriesAsync(session, work, candidate, providerId, now, creating: false, seriesNotices, omitted, cancellationToken);
        var recordClass = work.VersionClass ?? CatalogVersionClass.ForRecord(candidate);

        var evidence = await RecordEvidenceAsync(
            session, work, candidate, projection, series, ledger, providerId, ObservedOutcome, null, recordClass, creating: false, now, cancellationToken);
        return ObservationAttempt.Written(
            kind, work, providerId, candidate.ExternalId, referenceAdded: true, evidence, editionsAdded,
            series.Count(item => item.EntryAdded), new CatalogResolutionDiagnostics([], omitted), []);
    }

    // ------------------------------------------------------------------------------------------------------------ evidence

    /// <summary>Everything an observation records besides identity: claims and their selection, the snapshot and its retention.</summary>
    private static async Task<EvidenceEffects> RecordEvidenceAsync(
        ICatalogWriteSession session,
        Work work,
        BookCandidate candidate,
        EditionProjection projection,
        IReadOnlyList<SeriesBinding> series,
        ClaimLedger ledger,
        string providerId,
        string outcome,
        string? reason,
        string recordClass,
        bool creating,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;

        // The state of this source record's evidence before this attempt changes any of it, as the database stamps it. After a commit
        // of unknown outcome, "nothing has touched it since" is established by comparing this with the stored state, never by a time.
        var sourceRevision = creating
            ? string.Empty
            : await catalog.GetSourceRevisionAsync(work.Id, providerId, candidate.ExternalId, cancellationToken);

        var drafts = DraftClaims(candidate, projection, series, work).ToList();
        var resolvedScopes = series
            .Where(item => item.ResolvesUnboundScope is not null)
            .Select(item => item.ResolvesUnboundScope!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!creating)
        {
            // Every decision below is made over the complete claims of the facts this observation touches, never a prefix.
            await ledger.LoadAsync(
                drafts.Select(draft => new ClaimGroup(draft.Field, draft.ScopeKey))
                    .Concat(resolvedScopes.Select(scope => new ClaimGroup(MetadataClaimField.SeriesPosition, scope))),
                cancellationToken);
        }

        foreach (var draft in drafts)
        {
            ledger.Observe(draft);
        }

        foreach (var scope in resolvedScopes)
        {
            ledger.ResolveUnbound(scope, candidate.ExternalId);
        }

        var snapshot = await RecordSnapshotAsync(
            catalog, work, candidate, projection, outcome, reason, recordClass, series.Select(item => item.Disposition).ToArray(),
            creating, now, cancellationToken);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var ledgerOutcome = await ledger.FinalizeAsync(() => catalog.SaveChangesAsync(cancellationToken), today, cancellationToken);
        return new EvidenceEffects(
            ledgerOutcome.Effects,
            snapshot.Inserted ? snapshot.Hash : null,
            ledgerOutcome.ChangedAnything || snapshot.Inserted,
            sourceRevision);
    }

    private static IEnumerable<ClaimDraft> DraftClaims(
        BookCandidate candidate, EditionProjection projection, IReadOnlyList<SeriesBinding> series, Work work)
    {
        var source = candidate.ExternalId;
        if (candidate.PublicationDate is { } date)
        {
            yield return new ClaimDraft(
                MetadataClaimField.OriginalPublicationDate, ClaimLedger.WorkScope, source,
                EvidenceJson.DateValue(date, candidate.PublicationDatePrecision ?? DatePrecision.Day, candidate.PublicationDateBasis ?? DateBasis.Unknown));
        }

        foreach (var edition in projection.Compatible.Where(item => item.Isbn13 is not null && item.Edition.PublicationDate is not null))
        {
            // Only an edition this Work actually holds has a date to select; an excluded edition's date never reaches the Work.
            if (work.Editions.Any(held => held.Isbn13 == edition.Isbn13))
            {
                yield return new ClaimDraft(
                    MetadataClaimField.EditionPublicationDate, edition.Isbn13!, source,
                    EvidenceJson.DateValue(
                        edition.Edition.PublicationDate!.Value, edition.Edition.PublicationDatePrecision ?? DatePrecision.Day, DateBasis.Unknown));
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.Description))
        {
            yield return new ClaimDraft(MetadataClaimField.Description, ClaimLedger.WorkScope, source, EvidenceJson.TextValue("text", candidate.Description.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(candidate.CoverUrl))
        {
            yield return new ClaimDraft(MetadataClaimField.CoverUrl, ClaimLedger.WorkScope, source, EvidenceJson.TextValue("url", candidate.CoverUrl.Trim()));
        }

        if (candidate.PageCount is > 0)
        {
            yield return new ClaimDraft(MetadataClaimField.PageCount, ClaimLedger.WorkScope, source, EvidenceJson.NumberValue("pages", candidate.PageCount.Value));
        }

        foreach (var binding in series.Where(item => item.Claim is not null))
        {
            yield return new ClaimDraft(
                MetadataClaimField.SeriesPosition, binding.ScopeKey, source,
                EvidenceJson.SeriesPositionValue(binding.Claim!, SeriesOrdering.Of(binding.Claim!)));
        }
    }

    private static async Task<(bool Inserted, string Hash)> RecordSnapshotAsync(
        ICatalogMutationRepository catalog,
        Work work,
        BookCandidate candidate,
        EditionProjection projection,
        string outcome,
        string? reason,
        string recordClass,
        IReadOnlyList<string> seriesDispositions,
        bool creating,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (json, hash) = EvidenceJson.Snapshot(candidate, projection, outcome, reason, recordClass, seriesDispositions);
        var existing = creating
            ? []
            : (await catalog.ListObservationsAsync(work.Id, candidate.ProviderId, candidate.ExternalId, cancellationToken)).ToList();
        var same = existing.FirstOrDefault(observation => observation.ContentHash == hash);
        if (same is not null)
        {
            same.SeenAgain(now);
            return (false, hash);
        }

        var added = new SourceObservation(work.Id, candidate.ProviderId, candidate.ExternalId, hash, json, EvidenceJson.SchemaVersion, now);
        catalog.AddObservation(added);
        existing.Add(added);

        // First plus the latest three. A claim never references a snapshot, so pruning cannot remove a claim's source.
        var keep = new HashSet<SourceObservation> { existing.OrderBy(observation => observation.ObservedAtUtc).ThenBy(observation => observation.Id).First() };
        foreach (var recent in existing.OrderByDescending(observation => observation.ObservedAtUtc).ThenByDescending(observation => observation.Id).Take(3))
        {
            keep.Add(recent);
        }

        foreach (var pruned in existing.Where(observation => !keep.Contains(observation)).ToList())
        {
            catalog.RemoveObservation(pruned);
        }

        return (true, hash);
    }

    // ------------------------------------------------------------------------------------------------------------ editions

    private static int AddEditions(
        ICatalogMutationRepository catalog,
        Work work,
        EditionProjection projection,
        string providerId,
        EditionIdentityBasis basis,
        DateTimeOffset now,
        bool explicitAdd)
    {
        var heldIsbns = work.Editions.Where(edition => edition.Isbn13 is not null).Select(edition => edition.Isbn13!).ToHashSet(StringComparer.Ordinal);
        var heldKeys = work.Editions.Where(edition => edition.Isbn13 is null).Select(EditionKey).ToHashSet(StringComparer.Ordinal);
        var added = 0;

        foreach (var projected in projection.Compatible)
        {
            var source = projected.Edition;
            if (projected.Isbn13 is { } isbn ? !heldIsbns.Add(isbn) : !heldKeys.Add(EditionKey(source.Title, source.Format)))
            {
                continue;
            }

            var edition = new Edition(work.Id, source.Title, ToEditionFormat(source.Format), projected.Isbn13, source.PublicationDate, now);
            edition.ApplyObservation(source.Language, source.Publisher, basis, providerId, source.PublicationDatePrecision);
            if (explicitAdd)
            {
                catalog.AddEdition(edition);
            }

            work.AddEdition(edition);
            added++;
        }

        return added;
    }

    private static string EditionKey(Edition edition) => EditionKey(edition.Title, edition.Format.ToString());

    private static string EditionKey(string title, string format) =>
        CatalogText.NormalizeForMatch(string.IsNullOrWhiteSpace(title) ? "x" : title) + "|" + format.Trim().ToUpperInvariant();

    // ------------------------------------------------------------------------------------------------------------ authors

    private static async Task AddAuthorsAsync(
        ICatalogWriteSession session,
        Work work,
        BookCandidate candidate,
        string providerId,
        DateTimeOffset now,
        List<AuthorNotice> followerNotices,
        List<AmbiguousAuthorCredit> ambiguousCredits,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;
        foreach (var credit in candidate.Authors
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Select(name => name.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Select((name, ordinal) => (name, ordinal)))
        {
            var detail = candidate.AuthorCredits.FirstOrDefault(item =>
                CatalogIdentityPolicy.AuthorKey(item.Name) == CatalogIdentityPolicy.AuthorKey(credit.name));
            var sourceId = string.IsNullOrWhiteSpace(detail?.SourceAuthorId) ? null : detail!.SourceAuthorId!.Trim();
            var normalized = CatalogText.NormalizeForMatch(credit.name);

            var namesakes = await catalog.ListAuthorsByNormalizedNameAsync(normalized, cancellationToken);
            var referenceIds = sourceId is null
                ? []
                : await catalog.ListEntityIdsByExternalReferenceAsync(ExternalReferenceEntityType.Author, providerId, sourceId, cancellationToken);
            var otherId = sourceId is null
                ? new HashSet<Guid>()
                : await catalog.ListEntityIdsReferencedByOtherIdAsync(
                    ExternalReferenceEntityType.Author, providerId, sourceId, namesakes.Select(item => item.Id).ToArray(), cancellationToken);
            var decision = CatalogBindingPolicy.DecideAuthor(
                namesakes.Select(item => new NameMatch(item.Id, otherId.Contains(item.Id))).ToArray(), referenceIds, sourceId is not null);

            if (decision.Action == BindingAction.BindExisting)
            {
                var author = namesakes.FirstOrDefault(item => item.Id == decision.ExistingId)
                    ?? (await catalog.GetAuthorsAsync([decision.ExistingId!.Value], cancellationToken)).Single();
                work.AddAuthor(author, credit.ordinal, detail?.Role, decision.Basis);
                var followers = await session.Follows.ListFollowersAsync(FollowSubjectType.Author, author.Id, cancellationToken);
                followerNotices.AddRange(followers.Select(follower => new AuthorNotice(follower.UserId, author.Id, author.CanonicalName)));
                continue;
            }

            // A name alone proves no one of several namesakes: the credit gets its own Author for this Work, no namesake gains a
            // Work, and no follower of a namesake is told anything. The row says so (Provisional) instead of claiming a person.
            var created = new Author(credit.name, null, now);
            catalog.AddAuthor(created);
            work.AddAuthor(created, credit.ordinal, detail?.Role, decision.Basis);
            if (decision.Basis == AuthorBindingBasis.Created && sourceId is not null)
            {
                catalog.AddExternalReference(new ExternalReference(providerId, ExternalReferenceEntityType.Author, created.Id, sourceId, now));
            }

            if (decision.Basis == AuthorBindingBasis.Provisional)
            {
                ambiguousCredits.Add(new AmbiguousAuthorCredit(
                    created.CanonicalName, created.Id, namesakes.Select(namesake => namesake.Id).ToArray()));
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------ series

    /// <summary>
    /// Binds each series label the record states: to the one series it names (by source id, else by name), to a new series, or, when
    /// several saved series share the name and nothing selects one, nowhere: the membership stays an unbound claim, never an entry.
    /// </summary>
    private static async Task<IReadOnlyList<SeriesBinding>> BindSeriesAsync(
        ICatalogWriteSession session,
        Work work,
        BookCandidate candidate,
        string providerId,
        DateTimeOffset now,
        bool creating,
        List<SeriesNotice> followerNotices,
        List<OmittedSeriesMembership> omittedMemberships,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;
        var bindings = new List<SeriesBinding>();

        foreach (var stated in candidate.Series.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
        {
            var normalized = CatalogText.NormalizeForMatch(stated.Name);
            var sourceId = string.IsNullOrWhiteSpace(stated.SourceSeriesId) ? null : stated.SourceSeriesId.Trim();
            var effective = stated with { PositionSort = stated.PositionSort ?? TryParsePosition(stated.PositionLabel) };
            var hasPosition = effective.PositionSort is not null || !string.IsNullOrWhiteSpace(effective.PositionLabel);

            var namesakes = await catalog.ListSeriesByNormalizedNameAsync(normalized, cancellationToken);
            var referenceIds = sourceId is null
                ? []
                : await catalog.ListEntityIdsByExternalReferenceAsync(ExternalReferenceEntityType.Series, providerId, sourceId, cancellationToken);
            var otherId = sourceId is null
                ? new HashSet<Guid>()
                : await catalog.ListEntityIdsReferencedByOtherIdAsync(
                    ExternalReferenceEntityType.Series, providerId, sourceId, namesakes.Select(item => item.Id).ToArray(), cancellationToken);
            var decision = CatalogBindingPolicy.DecideSeries(
                namesakes.Select(item => new NameMatch(item.Id, otherId.Contains(item.Id))).ToArray(), referenceIds, sourceId is not null);

            if (decision.Action == BindingAction.LeaveUnbound)
            {
                omittedMemberships.Add(new OmittedSeriesMembership(
                    stated.Name.Trim(), namesakes.Select(namesake => namesake.Id).ToArray()));
                // The membership is real even when the source gives no number: the claim keeps the statement and no position is invented.
                bindings.Add(new SeriesBinding(UnboundScope(normalized), effective, "Unbound", false, null));
                continue;
            }

            Series bound;
            var isNew = decision.Action == BindingAction.CreateNew;
            if (isNew)
            {
                bound = new Series(stated.Name, stated.IsCompleted ? SeriesStatus.Completed : SeriesStatus.Unknown, now);
                catalog.AddSeries(bound);
                if (sourceId is not null)
                {
                    catalog.AddExternalReference(new ExternalReference(providerId, ExternalReferenceEntityType.Series, bound.Id, sourceId, now));
                }
            }
            else
            {
                bound = namesakes.FirstOrDefault(item => item.Id == decision.ExistingId)
                    ?? (await catalog.GetSeriesAsync([decision.ExistingId!.Value], cancellationToken)).Single();
                if (creating && stated.IsCompleted)
                {
                    bound.MarkCompleted(now);
                }
            }

            var entryAdded = false;
            if (work.SeriesEntries.All(entry => entry.SeriesId != bound.Id))
            {
                var entry = new SeriesEntry(bound, work, null, null, stated.IsPrimary, now);
                entry.MarkBinding(decision.Basis!.Value);
                if (!creating)
                {
                    catalog.AddSeriesEntry(entry);
                }

                work.AddSeriesEntry(entry);
                entryAdded = true;

                if (creating && !isNew)
                {
                    var followers = await session.Follows.ListFollowersAsync(FollowSubjectType.Series, bound.Id, cancellationToken);
                    followerNotices.AddRange(followers.Select(follower => new SeriesNotice(follower.UserId, bound.Id, bound.Name)));
                }
            }

            bindings.Add(new SeriesBinding(bound.Id.ToString("D"), hasPosition ? effective : null, "Bound", entryAdded, UnboundScope(normalized)));
        }

        return bindings;
    }

    private static string UnboundScope(string normalizedLabel)
    {
        const string prefix = "unbound:";
        return normalizedLabel.Length <= 200
            ? prefix + normalizedLabel
            : prefix + normalizedLabel[..160] + "#" + EvidenceJson.Hash(normalizedLabel)[..12];
    }

    // ------------------------------------------------------------------------------------------------------------ refresh

    /// <summary>
    /// An administrator refresh of one stored reference. The reference names the Work only: the fresh record is validated against
    /// the Work under the lock, and a contradiction keeps only a bounded evidence snapshot; it never moves a reference, creates a
    /// Work, adds an edition or changes a claim.
    /// </summary>
    private async Task<ObservationAttempt> RefreshAttemptAsync(
        ICatalogWriteSession session,
        RefreshIntent intent,
        BookCandidate candidate,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;
        var work = await catalog.GetWorkAsync(intent.WorkId, cancellationToken);
        if (work is null || work.IsRetired)
        {
            return ObservationAttempt.Nothing(ObservationKind.WorkRetired, work, "WorkRetired");
        }

        var references = await catalog.ListWorkReferencesAsync(work.Id, cancellationToken);
        if (!references.Any(reference => reference.ProviderId == intent.ProviderId && reference.ExternalId == intent.ExternalId))
        {
            // Fetched for a reference that is no longer on this Work: nothing here may be written.
            return ObservationAttempt.Nothing(ObservationKind.WorkRetired, work, "ReferenceNotOnWork");
        }

        if (!string.Equals(candidate.ExternalId, intent.ExternalId, StringComparison.Ordinal))
        {
            // The provider answered with a different record than the stored reference names: it is not evidence about this reference.
            return ObservationAttempt.Nothing(ObservationKind.NoProof, work, "RecordIdMismatch");
        }

        candidate = candidate with { ProviderId = intent.ProviderId };

        var now = clock.UtcNow;
        var intrinsic = CatalogEditionProjection.Project(candidate, null);
        var verdict = CatalogIdentityPolicy.EvaluateRecord(candidate, IdentityOf(work), intrinsic.Compatible);
        if (!verdict.IsCompatible)
        {
            var reason = verdict.Rejection!.Value.ToString();
            var sourceRevision = await catalog.GetSourceRevisionAsync(work.Id, intent.ProviderId, intent.ExternalId, cancellationToken);
            var contradiction = await RecordSnapshotAsync(
                catalog, work, candidate, intrinsic, "Contradiction", reason, CatalogVersionClass.ForRecord(candidate), [], false, now, cancellationToken);
            await catalog.SaveChangesAsync(cancellationToken);
            return ObservationAttempt.Contradicted(work, intent.ProviderId, intent.ExternalId, reason, contradiction.Inserted ? contradiction.Hash : null, sourceRevision);
        }

        var owners = await catalog.MapIsbnOwnersAsync(AllIsbns(candidate), cancellationToken);
        var targeted = ProjectAgainst(candidate, work, owners);
        var held = work.Editions.Where(edition => edition.Isbn13 is not null).Select(edition => edition.Isbn13!).ToHashSet(StringComparer.Ordinal);
        var contradicted = targeted.Excluded
            .Where(edition => edition.Isbn13 is not null && held.Contains(edition.Isbn13) &&
                edition.Exclusion is not (EditionExclusion.OwnedByOtherWork or EditionExclusion.LanguageConflict))
            .Select(edition => edition.Isbn13!)
            .ToArray();

        var ledger = new ClaimLedger(catalog, work, intent.ProviderId, now);
        var editionsAdded = AddEditions(catalog, work, targeted, intent.ProviderId, EditionIdentityBasis.Reference, now, explicitAdd: true);
        var seriesNotices = new List<SeriesNotice>();
        var omitted = new List<OmittedSeriesMembership>();
        var series = await BindSeriesAsync(session, work, candidate, intent.ProviderId, now, creating: false, seriesNotices, omitted, cancellationToken);

        var evidence = await RecordEvidenceAsync(
            session, work, candidate, targeted, series, ledger, intent.ProviderId, ObservedOutcome, null,
            work.VersionClass ?? CatalogVersionClass.ForRecord(candidate), creating: false, now, cancellationToken);
        return ObservationAttempt.Written(
            ObservationKind.Refreshed, work, intent.ProviderId, intent.ExternalId, referenceAdded: false, evidence, editionsAdded,
            series.Count(item => item.EntryAdded), new CatalogResolutionDiagnostics([], omitted), contradicted);
    }

    // ------------------------------------------------------------------------------------------------------------ discover

    /// <summary>
    /// A record from a source the Work has no reference to. It attaches only on ISBN proof that names exactly this Work and passes
    /// every record gate; every other plausible record is a proposal that is reported, not persisted.
    /// </summary>
    private async Task<ObservationAttempt> DiscoverAttemptAsync(
        ICatalogWriteSession session,
        DiscoverIntent intent,
        BookCandidate candidate,
        CancellationToken cancellationToken)
    {
        var catalog = session.Catalog;
        var work = await catalog.GetWorkAsync(intent.WorkId, cancellationToken);
        if (work is null || work.IsRetired)
        {
            return ObservationAttempt.Nothing(ObservationKind.WorkRetired, work, "WorkRetired");
        }

        var holder = await catalog.FindWorkByExternalReferenceAsync(candidate.ProviderId, candidate.ExternalId, cancellationToken);
        if (holder is not null)
        {
            return holder.Id == work.Id
                ? ObservationAttempt.Nothing(ObservationKind.Unchanged, work, "AlreadyAttached")
                : ObservationAttempt.Nothing(ObservationKind.Collision, work, "ReferenceOnOtherWork");
        }

        var intrinsic = CatalogEditionProjection.Project(candidate, null);
        var owners = await catalog.MapIsbnOwnersAsync(AllIsbns(candidate), cancellationToken);
        var proof = CatalogProof.Assess(intrinsic, owners);
        var identity = IdentityOf(work);

        switch (proof.Kind)
        {
            case ProofKind.MultipleWorks:
                return ObservationAttempt.Nothing(ObservationKind.Collision, work, "MultipleWorks");
            case ProofKind.ExcludedEditionOverlapOnly:
                return ObservationAttempt.Nothing(ObservationKind.NoProof, work, "ExcludedEditionOverlap");
            case ProofKind.SingleWork when proof.TargetWorkId != work.Id:
                return ObservationAttempt.Nothing(ObservationKind.Collision, work, "ProofNamesAnotherWork");
            case ProofKind.SingleWork:
                var verdict = CatalogIdentityPolicy.EvaluateRecord(candidate, identity, intrinsic.Compatible);
                if (!verdict.IsCompatible)
                {
                    return ObservationAttempt.Nothing(ObservationKind.Collision, work, verdict.Rejection!.Value.ToString());
                }

                var targeted = ProjectAgainst(candidate, work, owners);
                if (!targeted.ProofIsbns.Any(isbn => owners.TryGetValue(isbn, out var owner) && owner == work.Id))
                {
                    return ObservationAttempt.Nothing(ObservationKind.NoProof, work, "ExcludedEditionOverlap");
                }

                return await AttachAsync(session, work, candidate.ProviderId, candidate, targeted, EditionIdentityBasis.Isbn, ObservationKind.Attached, cancellationToken);
            default:
                var plausible = CatalogIdentityPolicy.EvaluateRecord(candidate, identity, intrinsic.Compatible);
                return plausible.IsCompatible
                    ? ObservationAttempt.Nothing(ObservationKind.Tentative, work, "NoSharedIsbn")
                    : ObservationAttempt.Nothing(ObservationKind.NoProof, work, plausible.Rejection!.Value.ToString());
        }
    }

    // ------------------------------------------------------------------------------------------------------------ admin refresh

    /// <summary>
    /// Administrator-only enrichment of one Work, the single place other sources are discovered. Each active stored reference is
    /// fetched at most once and re-validated under the catalog lock; each active provider without a reference gets at most one search
    /// and three detail fetches. Every provider's observation commits on its own, so a later failure, the sixty-second deadline or a
    /// cancellation keeps what already committed and reports the rest. Nothing here notifies anyone or removes any fact.
    /// </summary>
    public async Task<WorkRefreshReport> RefreshWorkAsync(Guid workId, CancellationToken cancellationToken)
    {
        var work = await reader.GetWorkAsync(workId, cancellationToken);
        if (work is null)
        {
            return new WorkRefreshReport(workId, WorkRefreshStatus.WorkNotFound, [], []);
        }

        if (work.IsRetired)
        {
            return new WorkRefreshReport(workId, WorkRefreshStatus.WorkRetired, [], []);
        }

        var references = await reader.GetWorkSourcesAsync(workId, cancellationToken);
        var active = (await providers.GetActiveProvidersAsync(cancellationToken))
            .ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(refreshDeadline ?? DefaultRefreshDeadline);

        var results = new List<ProviderRefreshResult>();
        var proposals = new List<TentativeProposal>();
        var partial = false;

        foreach (var reference in references)
        {
            if (!active.TryGetValue(reference.ProviderId, out var provider))
            {
                // Disabled, removed or never installed: nothing is asked and nothing is removed.
                results.Add(new ProviderRefreshResult(reference.ProviderId, reference.ExternalId, "Refresh", ProviderRefreshStatus.ProviderNotActive));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                results.Add(new ProviderRefreshResult(provider.Id, reference.ExternalId, "Refresh", ProviderRefreshStatus.DeadlineReached));
                partial = true;
                continue;
            }

            var (candidate, failure) = await FetchAsync(provider, reference.ExternalId, deadline, cancellationToken);
            if (failure is not null)
            {
                results.Add(new ProviderRefreshResult(provider.Id, reference.ExternalId, "Refresh", failure.Value));
                partial |= failure.Value != ProviderRefreshStatus.NotFound;
                continue;
            }

            var write = await writeScope.ExecuteAsync(
                (session, token) => RefreshAttemptAsync(session, new RefreshIntent(workId, provider.Id, reference.ExternalId), candidate!, token),
                VerifyAsync,
                cancellationToken,
                deadline.Token);
            var result = ToResult("Refresh", provider.Id, reference.ExternalId, write);
            results.Add(result);
            partial |= IsIncomplete(result.Status);
        }

        var referenced = references.Select(reference => reference.ProviderId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in active.Values.Where(provider => !referenced.Contains(provider.Id)).Take(MaximumDiscoveryProviders))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                results.Add(new ProviderRefreshResult(provider.Id, null, "Discover", ProviderRefreshStatus.DeadlineReached));
                partial = true;
                continue;
            }

            partial |= await DiscoverFromAsync(provider, work, deadline, results, proposals, cancellationToken);
        }

        return new WorkRefreshReport(workId, partial ? WorkRefreshStatus.Partial : WorkRefreshStatus.Completed, results, proposals);
    }

    /// <summary>One provider's discovery: at most one search and three detail fetches. Returns whether the step was incomplete.</summary>
    private async Task<bool> DiscoverFromAsync(
        IBookMetadataProvider provider,
        Work work,
        CancellationTokenSource deadline,
        List<ProviderRefreshResult> results,
        List<TentativeProposal> proposals,
        CancellationToken cancellationToken)
    {
        var text = DiscoveryQuery(work);
        if (text is null)
        {
            results.Add(new ProviderRefreshResult(provider.Id, null, "Discover", ProviderRefreshStatus.NoCandidate, "NoSearchableTitle"));
            return false;
        }

        BookCandidateSearchPage page;
        try
        {
            page = await provider.SearchAsync(new BookSearchQuery(text), deadline.Token);
        }
        catch (Exception exception) when (IsProviderFailure(exception, cancellationToken))
        {
            results.Add(new ProviderRefreshResult(provider.Id, null, "Discover", FailureOf(exception, deadline)));
            return true;
        }

        var heldIsbns = work.Editions.Where(edition => edition.Isbn13 is not null).Select(edition => edition.Isbn13!).ToHashSet(StringComparer.Ordinal);
        var workKey = CatalogIdentityPolicy.TitleKey(work.CanonicalTitle);
        var ranked = page.Candidates
            .Where(item => string.Equals(item.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
            .Select(item => (Item: item, Shares: item.Editions.Any(edition =>
                CatalogEditionProjection.NormalizeIsbn13(edition.Isbn13) is { } isbn && heldIsbns.Contains(isbn)),
                Titled: CatalogIdentityPolicy.TitleKey(item.Title) == workKey))
            .Where(entry => entry.Shares || entry.Titled)
            .OrderByDescending(entry => entry.Shares)
            .ThenByDescending(entry => entry.Titled)
            .ThenBy(entry => entry.Item.ExternalId, StringComparer.Ordinal)
            .Take(MaximumDiscoveryDetails)
            .Select(entry => entry.Item)
            .ToArray();
        if (ranked.Length == 0)
        {
            results.Add(new ProviderRefreshResult(provider.Id, null, "Discover", ProviderRefreshStatus.NoCandidate));
            return false;
        }

        var incomplete = false;
        foreach (var found in ranked)
        {
            if (deadline.IsCancellationRequested)
            {
                results.Add(new ProviderRefreshResult(provider.Id, found.ExternalId, "Discover", ProviderRefreshStatus.DeadlineReached));
                return true;
            }

            var (detail, failure) = await FetchAsync(provider, found.ExternalId, deadline, cancellationToken);
            if (failure is not null)
            {
                results.Add(new ProviderRefreshResult(provider.Id, found.ExternalId, "Discover", failure.Value));
                incomplete |= failure.Value != ProviderRefreshStatus.NotFound;
                continue;
            }

            var write = await writeScope.ExecuteAsync(
                (session, token) => DiscoverAttemptAsync(session, new DiscoverIntent(work.Id), detail!, token),
                VerifyAsync,
                cancellationToken,
                deadline.Token);
            var result = ToResult("Discover", provider.Id, detail!.ExternalId, write);
            results.Add(result);
            incomplete |= IsIncomplete(result.Status);
            if (result.Status == ProviderRefreshStatus.Tentative)
            {
                proposals.Add(new TentativeProposal(provider.Id, detail.ExternalId, detail.Title, detail.Authors, result.Reason ?? "NoSharedIsbn"));
            }

            if (result.Status == ProviderRefreshStatus.Attached)
            {
                // The provider now has its reference to this Work; its other records stay proposals at most.
                break;
            }
        }

        return incomplete;
    }

    private static string? DiscoveryQuery(Work work)
    {
        var author = work.Authors.OrderBy(credit => credit.Ordinal).Select(credit => credit.Author.CanonicalName).FirstOrDefault();
        var text = (author is null ? work.CanonicalTitle : $"{work.CanonicalTitle} {author}").Trim();
        if (text.Length > CatalogSearchLimits.MaximumQueryLength)
        {
            text = text[..CatalogSearchLimits.MaximumQueryLength].TrimEnd();
        }

        return CatalogSearchLimits.IsValidQueryText(text) ? text : null;
    }

    private static async Task<(BookCandidate? Candidate, ProviderRefreshStatus? Failure)> FetchAsync(
        IBookMetadataProvider provider,
        string externalId,
        CancellationTokenSource deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidate = await provider.GetDetailsAsync(externalId, deadline.Token);
            return candidate is null ? (null, ProviderRefreshStatus.NotFound) : (candidate, null);
        }
        catch (Exception exception) when (IsProviderFailure(exception, cancellationToken))
        {
            return (null, FailureOf(exception, deadline));
        }
    }

    /// <summary>An expected provider failure: transport, unreadable response, or a timeout. The caller's own cancellation is not one.</summary>
    private static bool IsProviderFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException;

    private static ProviderRefreshStatus FailureOf(Exception exception, CancellationTokenSource deadline) =>
        exception is OperationCanceledException && deadline.IsCancellationRequested
            ? ProviderRefreshStatus.DeadlineReached
            : ProviderRefreshStatus.ProviderUnavailable;

    private static bool IsIncomplete(ProviderRefreshStatus status) =>
        status is ProviderRefreshStatus.ProviderUnavailable or ProviderRefreshStatus.Busy or ProviderRefreshStatus.Conflict
            or ProviderRefreshStatus.OutcomeUnknown or ProviderRefreshStatus.CatalogUnavailable or ProviderRefreshStatus.DeadlineReached;

    private static ProviderRefreshResult ToResult(string phase, string providerId, string externalId, CatalogWriteResult<ObservationAttempt> write)
    {
        var status = write.Status switch
        {
            CatalogWriteStatus.Busy => ProviderRefreshStatus.Busy,
            CatalogWriteStatus.Conflict => ProviderRefreshStatus.Conflict,
            CatalogWriteStatus.OutcomeUnknown => ProviderRefreshStatus.OutcomeUnknown,
            CatalogWriteStatus.Unavailable => ProviderRefreshStatus.CatalogUnavailable,
            CatalogWriteStatus.DeadlineReached => ProviderRefreshStatus.DeadlineReached,
            _ => write.Value!.Kind switch
            {
                ObservationKind.Refreshed => ProviderRefreshStatus.Refreshed,
                ObservationKind.Unchanged => ProviderRefreshStatus.Unchanged,
                ObservationKind.Attached => ProviderRefreshStatus.Attached,
                ObservationKind.Contradiction => ProviderRefreshStatus.Contradiction,
                ObservationKind.Collision => ProviderRefreshStatus.Collision,
                ObservationKind.NoProof => ProviderRefreshStatus.NoProof,
                ObservationKind.Tentative => ProviderRefreshStatus.Tentative,
                ObservationKind.WorkRetired => ProviderRefreshStatus.WorkRetired,
                _ => ProviderRefreshStatus.Unchanged
            }
        };

        var attempt = write.Value;
        return new ProviderRefreshResult(
            providerId, externalId, phase, status, attempt?.Reason,
            attempt is { ContradictedIsbns.Count: > 0 } ? attempt.ContradictedIsbns : null);
    }

    // ------------------------------------------------------------------------------------------------------------ shared reads

    private static string[] AllIsbns(BookCandidate candidate) =>
        candidate.Editions
            .Select(edition => CatalogEditionProjection.NormalizeIsbn13(edition.Isbn13))
            .Where(isbn => isbn is not null)
            .Select(isbn => isbn!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static WorkIdentity IdentityOf(Work work) => new(
        work.CanonicalTitle,
        work.VersionClass,
        work.Editions.Select(edition => CatalogLanguage.Normalize(edition.Language)).Where(language => language is not null)
            .Select(language => language!).ToHashSet(StringComparer.Ordinal),
        work.Authors.OrderBy(author => author.Ordinal).Select(author => author.Author.CanonicalName).ToArray(),
        work.Editions.Select(edition => edition.Title).ToArray(),
        work.FirstPublicationDate,
        work.FirstPublicationBasis);

    /// <summary>The target-aware pass: an ISBN owned by a different Work, or a language the Work cannot hold, is out.</summary>
    private static EditionProjection ProjectAgainst(BookCandidate candidate, Work work, IReadOnlyDictionary<string, Guid> owners) =>
        CatalogEditionProjection.Project(candidate, ProjectionTarget.ForWork(
            work.VersionClass,
            work.Editions.Select(edition => CatalogLanguage.Normalize(edition.Language)).Where(language => language is not null)
                .Select(language => language!).ToHashSet(StringComparer.Ordinal),
            owners.Where(owner => owner.Value != work.Id).Select(owner => owner.Key).ToHashSet(StringComparer.Ordinal)));

    // ------------------------------------------------------------------------------------------------------------ unknown commit

    /// <summary>
    /// After a commit whose outcome is unknown, settles it under the same lock from what the database itself recorded, never from
    /// times the application noted (a clock may stand still, tie with another writer's, or run backwards).
    /// <list type="bullet">
    /// <item>The attempt's own transaction is recorded as committed: every effect it wrote happened, whatever later writers did to
    /// those rows since (superseded a claim, pruned a snapshot). Recovery reports the real outcome and never replays, so a later
    /// change is never overwritten with this attempt's older record.</item>
    /// <item>It is recorded as rolled back (or wrote nothing): none of it exists. It may be tried once more only if the source
    /// record's evidence is exactly as the attempt found it, by the revision the database stamps on those rows. If another writer
    /// has changed it, replaying would overwrite their later statement with this older one, so the outcome is reported unknown.</item>
    /// <item>The database no longer retains the transaction's status: neither outcome can be shown, so it is reported unknown.</item>
    /// </list>
    /// An old reference alone proves nothing about a refresh.
    /// </summary>
    private static async Task<ObservationAttempt?> VerifyAsync(
        ICatalogWriteSession session,
        ObservationAttempt attempted,
        CancellationToken cancellationToken)
    {
        if (attempted.Effects is not { HasContentEffects: true } effects)
        {
            // Nothing but last-seen bookkeeping (or nothing at all) was attempted: there is no content to prove or replay.
            return attempted;
        }

        var catalog = session.Catalog;
        switch (session.AttemptedTransaction)
        {
            case CatalogTransactionStatus.Committed:
                var work = await catalog.GetWorkAsync(effects.WorkId, cancellationToken)
                    ?? throw new CatalogOutcomeUnprovableException("The attempt's transaction committed, but its Work can no longer be read.");
                return attempted with { Work = work, WasCreated = false, AuthorNotices = [], SeriesNotices = [] };

            case CatalogTransactionStatus.NoWrites or CatalogTransactionStatus.RolledBack:
                var current = await catalog.GetSourceRevisionAsync(effects.WorkId, effects.ProviderId, effects.ExternalId, cancellationToken);
                if (!string.Equals(current, effects.SourceRevision, StringComparison.Ordinal))
                {
                    throw new CatalogOutcomeUnprovableException(
                        "The attempt did not commit, but another writer has changed this source record's evidence since, so it cannot safely be tried again.");
                }

                return null;

            default:
                throw new CatalogOutcomeUnprovableException("The database no longer records whether the attempt's transaction committed.");
        }
    }

    // ------------------------------------------------------------------------------------------------------------ notices

    /// <summary>Best-effort: a notification failure must never undo or fail an otherwise-successful catalog resolution.</summary>
    private async Task NotifyFollowersAsync(Work work, ObservationAttempt attempt, CancellationToken cancellationToken)
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

    // ------------------------------------------------------------------------------------------------------------ helpers

    private static EditionFormat ToEditionFormat(string format) =>
        (format ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "HARDCOVER" => EditionFormat.Hardcover,
            "PAPERBACK" => EditionFormat.Paperback,
            "EBOOK" or "E-BOOK" => EditionFormat.Ebook,
            "AUDIOBOOK" or "AUDIO BOOK" => EditionFormat.Audiobook,
            "" or "UNKNOWN FORMAT" => EditionFormat.Unknown,
            _ => EditionFormat.Other
        };

    private static decimal? TryParsePosition(string? positionLabel) =>
        decimal.TryParse(positionLabel, NumberStyles.Number, CultureInfo.InvariantCulture, out var position) ? position : null;

    private static void ValidateProviderReference(string providerId, string externalId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 128 ||
            string.IsNullOrWhiteSpace(externalId) || externalId.Length > 512)
        {
            throw new ArgumentException("The provider reference is invalid.");
        }
    }
}

internal sealed record AuthorNotice(Guid FollowerUserId, Guid AuthorId, string AuthorName);

internal sealed record SeriesNotice(Guid FollowerUserId, Guid SeriesId, string SeriesName);

/// <param name="ScopeKey">The series id for a bound series, <c>unbound:&lt;label&gt;</c> for one that could not be bound.</param>
/// <param name="Claim">The statement to keep as a claim. An unbound membership always has one (its position may be unknown); a bound one has none when the record states no position.</param>
/// <param name="Disposition">Whether the label bound to a series (Bound) or stayed an unbound claim (Unbound). Deliberately coarse: how it bound (name, reference, creation) is stored on the entry and differs between a first and a later observation of identical content.</param>
/// <param name="EntryAdded">Whether this observation created the Work's membership of the series.</param>
/// <param name="ResolvesUnboundScope">For a bound membership, the unbound claim scope of the same label: this source's current statement there is resolved.</param>
internal sealed record SeriesBinding(string ScopeKey, BookSeriesCandidate? Claim, string Disposition, bool EntryAdded, string? ResolvesUnboundScope);

/// <summary>
/// What recording evidence did: the claim values it wrote, the snapshot if one was inserted, and the source record's database-stamped
/// revision from before the attempt changed anything.
/// </summary>
internal sealed record EvidenceEffects(IReadOnlyList<ClaimKey> Claims, string? NewSnapshotHash, bool ChangedContent, string SourceRevision);

/// <summary>
/// What one attempt wrote, and the source record's evidence revision it started from. After a commit of unknown outcome the
/// database's own record of the attempt's transaction says whether any of it is there; this revision says, when it is not, whether
/// anything else has changed the record since (in which case the attempt must not be replayed over it). Material changes include
/// selected values updated from existing evidence, even when no snapshot or claim value was added.
/// </summary>
internal sealed record ExpectedEffects(
    Guid WorkId,
    string ProviderId,
    string ExternalId,
    bool ReferenceAdded,
    string? NewSnapshotHash,
    IReadOnlyList<ClaimKey> Claims,
    string SourceRevision,
    bool MaterialContentChanged)
{
    public bool HasContentEffects => MaterialContentChanged || ReferenceAdded || NewSnapshotHash is not null || Claims.Count > 0;
}

/// <summary>What one write attempt decided. Attempt-local by construction: an abandoned attempt's instance is simply discarded.</summary>
internal sealed record ObservationAttempt(
    ObservationKind Kind,
    Work? Work,
    bool WasCreated,
    CatalogResolutionFault? Fault,
    ExpectedEffects? Effects,
    IReadOnlyList<AuthorNotice> AuthorNotices,
    IReadOnlyList<SeriesNotice> SeriesNotices,
    CatalogResolutionDiagnostics Diagnostics,
    ObservationDetail? Detail,
    string? Reason,
    IReadOnlyList<string> ContradictedIsbns)
{
    public static ObservationAttempt Found(Work work) =>
        new(ObservationKind.Existing, work, false, null, null, [], [], CatalogResolutionDiagnostics.None, null, null, []);

    public static ObservationAttempt Failed(CatalogResolutionFault fault) =>
        new(ObservationKind.Existing, null, false, fault, null, [], [], CatalogResolutionDiagnostics.None, null, null, []);

    /// <summary>A step that wrote nothing: a refusal, a proposal, or a retired Work.</summary>
    public static ObservationAttempt Nothing(ObservationKind kind, Work? work, string? reason, IReadOnlyList<string>? contradictedIsbns = null) =>
        new(kind, work, false, null, null, [], [], CatalogResolutionDiagnostics.None, null, reason, contradictedIsbns ?? []);

    public static ObservationAttempt Created(
        Work work,
        string providerId,
        string externalId,
        EvidenceEffects evidence,
        IReadOnlyList<AuthorNotice> authorNotices,
        IReadOnlyList<SeriesNotice> seriesNotices,
        CatalogResolutionDiagnostics resolutionDiagnostics,
        ObservationDetail? detail) =>
        new(
            ObservationKind.Created, work, true, null,
            new ExpectedEffects(work.Id, providerId, externalId, true, evidence.NewSnapshotHash, evidence.Claims, evidence.SourceRevision, true),
            authorNotices, seriesNotices, resolutionDiagnostics, detail, detail?.Reason, []);

    /// <summary>
    /// An attach or a known-reference refresh. A refresh that added nothing beyond last-seen bookkeeping is <c>Unchanged</c>.
    /// </summary>
    public static ObservationAttempt Written(
        ObservationKind kind,
        Work work,
        string providerId,
        string externalId,
        bool referenceAdded,
        EvidenceEffects evidence,
        int editionsAdded,
        int seriesEntriesAdded,
        CatalogResolutionDiagnostics resolutionDiagnostics,
        IReadOnlyList<string> contradictedIsbns)
    {
        var changed = referenceAdded || evidence.ChangedContent || editionsAdded > 0 || seriesEntriesAdded > 0;
        var resolved = kind == ObservationKind.Refreshed && !changed ? ObservationKind.Unchanged : kind;
        return new(
            resolved, work, false, null,
            new ExpectedEffects(work.Id, providerId, externalId, referenceAdded, evidence.NewSnapshotHash, evidence.Claims, evidence.SourceRevision, changed),
            [], [], resolutionDiagnostics, null, null, contradictedIsbns);
    }

    /// <summary>A refresh whose record contradicts its Work: only the evidence snapshot (if it is new) was written.</summary>
    public static ObservationAttempt Contradicted(
        Work work, string providerId, string externalId, string reason, string? newSnapshotHash, string sourceRevision) =>
        new(
            ObservationKind.Contradiction, work, false, null,
            new ExpectedEffects(work.Id, providerId, externalId, false, newSnapshotHash, [], sourceRevision, false),
            [], [], CatalogResolutionDiagnostics.None, null, reason, []);
}
