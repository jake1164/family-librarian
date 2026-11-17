using System.Globalization;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Observation;

/// <summary>One source statement about one Work fact, before it is stored. The hash is derived from the value text.</summary>
internal sealed record ClaimDraft(MetadataClaimField Field, string ScopeKey, string SourceEntityId, string ValueJson)
{
    public string ValueHash => EvidenceJson.Hash(ValueJson);
}

internal enum ClaimChange
{
    Inserted,
    Reinstated,
    SeenAgain
}

/// <summary>The natural key of a claim value an attempt wrote (inserted, or revisited as history and made current again).</summary>
internal sealed record ClaimKey(
    MetadataClaimField Field, string ScopeKey, string ProviderId, string SourceEntityId, string ValueHash);

/// <summary>What recording and selecting claims changed, beyond last-seen bookkeeping.</summary>
internal sealed record LedgerOutcome(bool ClaimsChanged, bool SelectionChanged, IReadOnlyList<ClaimKey> Effects)
{
    public bool ChangedAnything => ClaimsChanged || SelectionChanged;
}

/// <summary>
/// The claim store of one Work for one observation attempt: records a source's statements next to every competing one, selects the
/// displayed value deterministically, and compacts what is no longer needed. It works on the attempt's own tracked entities and is
/// only used inside the catalog write scope, so every decision is made from the claims visible after the lock was taken.
/// </summary>
internal sealed class ClaimLedger(
    ICatalogMutationRepository catalog,
    Work work,
    string providerId,
    DateTimeOffset now)
{
    /// <summary>Scope of a Work-level fact (a date, a description). Series and edition facts use a series id or an ISBN-13.</summary>
    internal const string WorkScope = "work";

    /// <summary>The pseudo-source a legacy, never-attributed value participates as, once something else says something.</summary>
    internal const string LegacyProvider = "legacy-unattributed";

    /// <summary>At most this many superseded claims survive beside the current, selected and first one of a source key.</summary>
    internal const int MaximumSupersededKept = 8;

    private readonly List<MetadataClaim> claims = [];
    private readonly HashSet<ClaimGroup> loaded = [];
    private readonly HashSet<(MetadataClaimField Field, string Scope)> touched = [];
    private readonly HashSet<(MetadataClaimField Field, string Scope, string Provider, string Source)> touchedKeys = [];
    private readonly List<ClaimKey> effects = [];
    private bool claimsChanged;

    /// <summary>The claims of the Work as the ledger sees them (tracked).</summary>
    internal IReadOnlyList<MetadataClaim> Claims => claims;

    /// <summary>
    /// Reads the complete claims of every fact this observation will touch, once, before anything is decided. The decisions below
    /// (supersede, select, compact) are only correct over a fact's complete claim set, so nothing here is a capped or partial read.
    /// A brand-new Work has no claims and never calls this.
    /// </summary>
    internal async Task LoadAsync(IEnumerable<ClaimGroup> groups, CancellationToken cancellationToken)
    {
        var missing = groups.Where(group => !loaded.Contains(group)).Distinct().ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        claims.AddRange(await catalog.ListClaimsAsync(work.Id, missing, cancellationToken));
        loaded.UnionWith(missing);
    }

    /// <summary>
    /// This source no longer leaves a membership unresolved: it now binds. The source's current unbound statement stops being
    /// current (it stays as history, never deleted), so the issue report no longer lists a membership that was resolved.
    /// </summary>
    internal void ResolveUnbound(string unboundScope, string sourceEntityId)
    {
        var resolved = false;
        foreach (var claim in claims.Where(claim =>
                     claim.Field == MetadataClaimField.SeriesPosition && claim.ScopeKey == unboundScope &&
                     claim.ProviderId == providerId && claim.SourceEntityId == sourceEntityId && claim.IsCurrent))
        {
            claim.Supersede();
            resolved = true;
        }

        if (resolved)
        {
            claimsChanged = true;
            touched.Add((MetadataClaimField.SeriesPosition, unboundScope));
        }
    }

    internal ClaimChange Observe(ClaimDraft draft)
    {
        EnsureLegacyClaim(draft.Field, draft.ScopeKey);
        touched.Add((draft.Field, draft.ScopeKey));
        touchedKeys.Add((draft.Field, draft.ScopeKey, providerId, draft.SourceEntityId));

        var source = claims.Where(claim =>
            claim.Field == draft.Field && claim.ScopeKey == draft.ScopeKey &&
            claim.ProviderId == providerId && claim.SourceEntityId == draft.SourceEntityId).ToList();
        var hash = draft.ValueHash;
        var same = source.FirstOrDefault(claim => claim.ValueHash == hash);
        if (same is { IsCurrent: true })
        {
            same.SeenAgain(now);
            return ClaimChange.SeenAgain;
        }

        foreach (var current in source.Where(claim => claim.IsCurrent))
        {
            current.Supersede();
        }

        claimsChanged = true;
        if (same is not null)
        {
            // The same source returned to a value it stated before: its row is revisited, not duplicated.
            same.Reinstate(now);
            effects.Add(new ClaimKey(draft.Field, draft.ScopeKey, providerId, draft.SourceEntityId, hash));
            return ClaimChange.Reinstated;
        }

        var added = new MetadataClaim(work.Id, draft.Field, draft.ScopeKey, providerId, draft.SourceEntityId, draft.ValueJson, hash, now);
        claims.Add(added);
        catalog.AddClaim(added);
        effects.Add(new ClaimKey(draft.Field, draft.ScopeKey, providerId, draft.SourceEntityId, hash));
        return ClaimChange.Inserted;
    }

    /// <summary>
    /// Selects the displayed value of every fact touched, applies it to the Work, its entries and editions, and compacts. Selection
    /// moves in two saves inside the caller's transaction: a partial unique index allows one selected claim per fact, so the old one
    /// is cleared before the new one is set.
    /// </summary>
    internal async Task<LedgerOutcome> FinalizeAsync(Func<Task> save, DateOnly today, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var moved = new List<MetadataClaim>();
        var selectionChanged = false;

        foreach (var (field, scope) in touched.OrderBy(group => group.Field).ThenBy(group => group.Scope, StringComparer.Ordinal))
        {
            var current = claims.Where(claim => claim.Field == field && claim.ScopeKey == scope).ToList();
            var desired = Desired(field, scope, current);
            var selected = current.FirstOrDefault(claim => claim.IsSelected);
            if (desired is null || ReferenceEquals(desired, selected))
            {
                continue;
            }

            if (selected is null)
            {
                // Nothing is selected for this fact yet: the claim is inserted already selected, in the same save.
                desired.Select();
                selectionChanged = true;
            }
            else
            {
                selected.Deselect();
                moved.Add(desired);
            }
        }

        if (moved.Count > 0)
        {
            // A partial unique index allows one selected claim per fact, so the old selection is cleared and saved first.
            await save();
            foreach (var claim in moved)
            {
                claim.Select();
            }

            selectionChanged = true;
        }

        foreach (var (field, scope) in touched.OrderBy(group => group.Field).ThenBy(group => group.Scope, StringComparer.Ordinal))
        {
            selectionChanged |= ApplySelection(field, scope, today);
        }

        Compact();
        await save();
        return new LedgerOutcome(claimsChanged, selectionChanged, effects);
    }

    // ---------------------------------------------------------------------------------------------------- legacy pseudo-claims

    private void EnsureLegacyClaim(MetadataClaimField field, string scope)
    {
        if (claims.Any(claim => claim.Field == field && claim.ScopeKey == scope))
        {
            return;
        }

        if (field == MetadataClaimField.SeriesPosition && Guid.TryParse(scope, out var seriesId) &&
            work.SeriesEntries.FirstOrDefault(entry => entry.SeriesId == seriesId) is { } entry &&
            (entry.PositionLabel is not null || entry.PositionSort is not null))
        {
            var series = new BookSeriesCandidate(entry.Series?.Name ?? scope, entry.PositionLabel, entry.IsPrimary, entry.PositionSort);
            AddLegacy(field, scope, EvidenceJson.SeriesPositionValue(series, SeriesOrderingKind.Unspecified), entry.CreatedAtUtc);
        }
        else if (field == MetadataClaimField.OriginalPublicationDate && scope == WorkScope && work.FirstPublicationDate is { } date)
        {
            // A missing precision or basis is unknown, not a day or an original: the pseudo-claim says so, and being only a stand-in
            // for what the Work already holds it is never written back over the Work (see ApplyWorkDate).
            AddLegacy(
                field,
                scope,
                EvidenceJson.LegacyDateValue(date, work.FirstPublicationPrecision, work.FirstPublicationBasis),
                work.CreatedAtUtc);
        }
    }

    private void AddLegacy(MetadataClaimField field, string scope, string valueJson, DateTimeOffset firstSeen)
    {
        // The value is real (it is what the catalog stored); only its source is unknown, and the claim says exactly that.
        var legacy = new MetadataClaim(
            work.Id, field, scope, LegacyProvider, work.Id.ToString("D"), valueJson, EvidenceJson.Hash(valueJson), firstSeen);
        claims.Add(legacy);
        catalog.AddClaim(legacy);
        touchedKeys.Add((field, scope, LegacyProvider, work.Id.ToString("D")));
        claimsChanged = true;
        effects.Add(new ClaimKey(field, scope, LegacyProvider, work.Id.ToString("D"), legacy.ValueHash));
    }

    // ---------------------------------------------------------------------------------------------------- selection

    private MetadataClaim? Desired(MetadataClaimField field, string scope, List<MetadataClaim> group)
    {
        var current = group.Where(claim => claim.IsCurrent).ToList();
        switch (field)
        {
            case MetadataClaimField.SeriesPosition:
            {
                var selection = SeriesPositionSelection.Select(current.Select(ToPosition).ToArray());
                return selection is null ? null : current.First(claim =>
                    claim.ProviderId == selection.Selected.ProviderId && claim.SourceEntityId == selection.Selected.SourceEntityId);
            }

            case MetadataClaimField.OriginalPublicationDate:
            case MetadataClaimField.EditionPublicationDate:
            {
                var selection = PublicationDateSelection.Select(current.Select(claim => ToDate(claim, field)).ToArray());
                return selection is null ? null : current.First(claim =>
                    claim.ProviderId == selection.Selected.ProviderId && claim.SourceEntityId == selection.Selected.SourceEntityId);
            }

            default:
                return DesiredScalar(field, group, current);
        }
    }

    /// <summary>
    /// A fill-only scalar is first-accepted and sticky: it moves only when the very source that supplied it states something else,
    /// and it is never taken from a legacy value that has no claim. Which source fills a gap depends on arrival order; that is the
    /// stated rule, not an accident.
    /// </summary>
    private MetadataClaim? DesiredScalar(MetadataClaimField field, List<MetadataClaim> group, List<MetadataClaim> current)
    {
        var selected = group.FirstOrDefault(claim => claim.IsSelected);
        if (selected is not null)
        {
            if (selected.IsCurrent)
            {
                return selected;
            }

            // Its own source replaced it: the selection follows that source's new statement.
            return current.FirstOrDefault(claim => claim.ProviderId == selected.ProviderId && claim.SourceEntityId == selected.SourceEntityId)
                ?? selected;
        }

        var stored = field switch
        {
            MetadataClaimField.Description => work.Description is not null,
            MetadataClaimField.CoverUrl => work.CoverUrl is not null,
            MetadataClaimField.PageCount => work.CatalogPageCount is not null,
            _ => true
        };
        return stored
            ? null
            : current.OrderBy(claim => claim.FirstSeenAtUtc).ThenBy(claim => claim.ProviderId, StringComparer.Ordinal)
                .ThenBy(claim => claim.SourceEntityId, StringComparer.Ordinal).ThenBy(claim => claim.ValueHash, StringComparer.Ordinal)
                .FirstOrDefault();
    }

    private bool ApplySelection(MetadataClaimField field, string scope, DateOnly today)
    {
        var group = claims.Where(claim => claim.Field == field && claim.ScopeKey == scope).ToList();
        var selected = group.FirstOrDefault(claim => claim.IsSelected);
        if (selected is null)
        {
            return false;
        }

        switch (field)
        {
            case MetadataClaimField.SeriesPosition:
                return ApplySeriesPosition(scope, group);
            case MetadataClaimField.OriginalPublicationDate:
                return ApplyWorkDate(selected, today);
            case MetadataClaimField.EditionPublicationDate:
                return ApplyEditionDate(scope, selected);
            case MetadataClaimField.Description:
                return ApplyScalar(Text(selected, "text"), work.Description, text => work.ReplaceDescription(text, now));
            case MetadataClaimField.CoverUrl:
                return ApplyScalar(Text(selected, "url"), work.CoverUrl, url => work.ReplaceCoverUrl(url, now));
            default:
                return ApplyPageCount(selected);
        }
    }

    private bool ApplySeriesPosition(string scope, List<MetadataClaim> group)
    {
        if (!Guid.TryParse(scope, out var seriesId) || work.SeriesEntries.FirstOrDefault(entry => entry.SeriesId == seriesId) is not { } entry)
        {
            return false;
        }

        var selection = SeriesPositionSelection.Select(group.Where(claim => claim.IsCurrent).Select(ToPosition).ToArray());
        if (selection is null)
        {
            return false;
        }

        var label = selection.Selected.Label;
        var sort = selection.Selected.Sort;
        if (entry.PositionLabel == label && entry.PositionSort == sort && entry.PositionState == selection.State)
        {
            return false;
        }

        entry.ApplyPosition(label, sort, selection.State, now);
        return true;
    }

    private bool ApplyWorkDate(MetadataClaim selected, DateOnly today)
    {
        if (selected.ProviderId == LegacyProvider)
        {
            // The legacy value is the Work's own date, stood in for so that others can be compared with it. It carries exactly what
            // the Work holds (including an unknown precision, basis and status), so there is nothing to write back, and writing its
            // placeholder precision would turn "unknown" into "an exact day".
            return false;
        }

        var claim = ToDate(selected, MetadataClaimField.OriginalPublicationDate);
        var status = PublicationStatusPolicy.Evaluate(claim.Date, claim.Precision, today);
        if (work.FirstPublicationDate == claim.Date && work.FirstPublicationPrecision == claim.Precision &&
            work.FirstPublicationBasis == claim.Basis && work.PublicationStatus == status)
        {
            return false;
        }

        work.SetFirstPublication(claim.Date, claim.Precision, claim.Basis, status, now);
        return true;
    }

    private bool ApplyEditionDate(string isbn, MetadataClaim selected)
    {
        var edition = work.Editions.FirstOrDefault(item => item.Isbn13 == isbn);
        if (edition is null)
        {
            return false;
        }

        var claim = ToDate(selected, MetadataClaimField.EditionPublicationDate);
        if (edition.PublicationDate == claim.Date && edition.PublicationPrecision == claim.Precision)
        {
            return false;
        }

        edition.SetPublicationDate(claim.Date, claim.Precision, now);
        return true;
    }

    private static bool ApplyScalar(string? selected, string? stored, Action<string?> replace)
    {
        if (selected is null || string.Equals(selected, stored, StringComparison.Ordinal))
        {
            return false;
        }

        replace(selected);
        return true;
    }

    private bool ApplyPageCount(MetadataClaim selected)
    {
        using var document = JsonDocument.Parse(selected.ValueJson);
        if (!document.RootElement.TryGetProperty("pages", out var pages) || !pages.TryGetInt32(out var value) || value <= 0 ||
            work.CatalogPageCount == value)
        {
            return false;
        }

        if (work.CatalogPageCount is null)
        {
            work.SetCatalogLength(value, null, selected.ProviderId, now);
        }
        else
        {
            work.ReplaceCatalogPageCount(value, selected.ProviderId, now);
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------------------- compaction

    /// <summary>
    /// Per source key keeps the current, the selected and the first-ever claim plus the latest eight superseded ones: at most
    /// eleven rows. Nothing that is current or selected is ever removed, so no selected value loses its evidence.
    /// </summary>
    private bool Compact()
    {
        var removed = false;
        foreach (var (field, scope, provider, source) in touchedKeys)
        {
            var key = claims.Where(claim => claim.Field == field && claim.ScopeKey == scope &&
                claim.ProviderId == provider && claim.SourceEntityId == source).ToList();
            var keep = key.Where(claim => claim.IsCurrent || claim.IsSelected).ToHashSet();
            if (key.Count > 0)
            {
                keep.Add(key.OrderBy(claim => claim.FirstSeenAtUtc).ThenBy(claim => claim.Id).First());
            }

            foreach (var recent in key.Where(claim => !keep.Contains(claim))
                         .OrderByDescending(claim => claim.LastSeenAtUtc).ThenByDescending(claim => claim.Id)
                         .Take(MaximumSupersededKept).ToList())
            {
                keep.Add(recent);
            }

            foreach (var claim in key.Where(claim => !keep.Contains(claim)).ToList())
            {
                claims.Remove(claim);
                catalog.RemoveClaim(claim);
                removed = true;
            }
        }

        return removed;
    }

    // ---------------------------------------------------------------------------------------------------- value reading

    private static PositionClaim ToPosition(MetadataClaim claim)
    {
        using var document = JsonDocument.Parse(claim.ValueJson);
        var root = document.RootElement;
        var label = root.TryGetProperty("label", out var labelValue) && labelValue.ValueKind == JsonValueKind.String ? labelValue.GetString() : null;
        decimal? sort = root.TryGetProperty("sort", out var sortValue) && sortValue.ValueKind == JsonValueKind.String &&
            decimal.TryParse(sortValue.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        return new PositionClaim(claim.ProviderId, claim.SourceEntityId, label, sort);
    }

    private static DateClaim ToDate(MetadataClaim claim, MetadataClaimField field)
    {
        using var document = JsonDocument.Parse(claim.ValueJson);
        var root = document.RootElement;
        var date = DateOnly.ParseExact(root.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var precision = Enum.Parse<DatePrecision>(root.GetProperty("precision").GetString()!);
        var basis = field == MetadataClaimField.OriginalPublicationDate && root.TryGetProperty("basis", out var basisValue)
            ? Enum.Parse<DateBasis>(basisValue.GetString()!)
            : DateBasis.Unknown;
        return new DateClaim(claim.ProviderId, claim.SourceEntityId, date, precision, basis);
    }

    private static string? Text(MetadataClaim claim, string property)
    {
        using var document = JsonDocument.Parse(claim.ValueJson);
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
