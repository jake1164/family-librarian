using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Domain.Catalog;
using PositionClaim = FamilyLibrarian.Application.Catalog.Discovery.PositionClaim;

namespace FamilyLibrarian.Application.Catalog.Answers;

/// <summary>
/// Maps what the shared reads return (discovery items, ordinary candidates, saved rows) onto <see cref="CatalogAnswerEntry"/>.
/// Mapping only restates evidence: it never infers a date's meaning, a role, a class or a native id, and never merges two records.
/// </summary>
public static class CatalogAnswerEntries
{
    /// <summary>The class a version label supports. "Unspecified" means a classifier found no marker; that is not a verified novel.</summary>
    public static CatalogWorkClass ClassOf(string? versionKind) => versionKind switch
    {
        "Novel" => CatalogWorkClass.Novel,
        "Collection" => CatalogWorkClass.Collection,
        "Abridged" => CatalogWorkClass.Abridgement,
        "Adaptation" => CatalogWorkClass.Adaptation,
        "GraphicAdaptation" => CatalogWorkClass.GraphicAdaptation,
        "StudyGuide" => CatalogWorkClass.StudyGuide,
        _ => CatalogWorkClass.Unknown
    };

    public static CatalogAnswerEntry FromMember(SeriesMember member, string? seriesLabel)
    {
        ArgumentNullException.ThrowIfNull(member);
        return FromDiscovered(member.Book, member.Positions, member.Role, seriesLabel);
    }

    public static CatalogAnswerEntry FromAuthorWork(AuthorWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var positions = work.Series.Where(claim => claim.Position is not null).Select(claim => claim.Position!).ToArray();
        return FromDiscovered(work.Book, positions, MembershipRole.Unknown, null);
    }

    private static CatalogAnswerEntry FromDiscovered(
        DiscoveredBook book, IReadOnlyList<PositionClaim> positions, MembershipRole role, string? seriesLabel) => new()
    {
        Source = book.Source,
        ProviderId = book.Source.ProviderId,
        Title = book.Title.Reported,
        Credits = book.Credits.Select(credit => new CatalogEntryCredit(credit.Ordinal, credit.Label, credit.Role, credit.AuthorId, null)).ToArray(),
        Positions = positions.ToArray(),
        Role = role,
        Class = ClassOf(book.Version?.Kind),
        Language = book.Language,
        Format = book.Format,
        Publication = book.Publication,
        ObservedAtUtc = book.ObservedAtUtc,
        SeriesLabel = seriesLabel
    };

    /// <summary>
    /// A seed-search candidate as an entry. A record whose adapter did not say whether its id is a Work or an Edition has no usable
    /// identity and is not an entry (null): a guessed kind would let it stand in for a record it is not.
    /// </summary>
    public static CatalogAnswerEntry? FromCandidate(BookCandidate candidate, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.SourceKind is not { } kind || !SourceRef.TryCreate(candidate.ProviderId, candidate.ExternalId, kind, out var source))
        {
            return null;
        }

        var publication = candidate.PublicationDate is { } date
            // A null precision is the historical meaning of the date: a full day. A null basis is unknown, never original.
            ? PublicationEvidence.Dated(date, candidate.PublicationDatePrecision ?? DatePrecision.Day, candidate.PublicationDateBasis ?? DateBasis.Unknown)
            : PublicationEvidence.NotStated;

        var credits = new List<CatalogEntryCredit>();
        if (candidate.AuthorCredits.Count > 0)
        {
            for (var ordinal = 0; ordinal < candidate.AuthorCredits.Count; ordinal++)
            {
                var credit = candidate.AuthorCredits[ordinal];
                SourceRef.TryCreate(candidate.ProviderId, credit.SourceAuthorId, ExternalReferenceEntityType.Author, out var author);
                credits.Add(new CatalogEntryCredit(ordinal, credit.Name, credit.Role, author, null));
            }
        }
        else
        {
            for (var ordinal = 0; ordinal < candidate.Authors.Count; ordinal++)
            {
                credits.Add(new CatalogEntryCredit(ordinal, candidate.Authors[ordinal], null, null, null));
            }
        }

        var positions = candidate.Series
            .Where(series => series.PositionSort is not null || !string.IsNullOrWhiteSpace(series.PositionLabel))
            .Select(series =>
            {
                SourceRef.TryCreate(candidate.ProviderId, series.SourceSeriesId, ExternalReferenceEntityType.Series, out var seriesRef);
                var ordering = SeriesOrdering.Of(series);
                return new PositionClaim(series.PositionLabel, series.PositionSort, ordering == SeriesOrderingKind.Unspecified ? null : ordering, seriesRef);
            })
            .ToArray();

        return new CatalogAnswerEntry
        {
            Source = source,
            ProviderId = candidate.ProviderId,
            Title = candidate.Title,
            Credits = credits,
            Positions = positions,
            Class = ClassOf(BookCandidateVersion.Assess(candidate).Kind),
            Language = candidate.Language,
            Publication = publication,
            ObservedAtUtc = observedAtUtc,
            SeriesLabel = candidate.Series is [var firstSeries, ..] ? firstSeries.Name : null
        };
    }

    public static CatalogAnswerEntry FromSaved(SavedWorkEvidence work, IReadOnlyList<PositionClaim> positions, string? seriesLabel)
    {
        ArgumentNullException.ThrowIfNull(work);
        var legacy = false;
        PublicationEvidence publication;
        if (work.Date is { } date)
        {
            // A legacy row has no recorded precision: the year is its widest reading, so no exact day is invented from it.
            // No basis is read as Unknown, never as the original date.
            legacy = work.Precision is null;
            // The saved status is computed from the date at write time, not a source's own statement, so it is not carried as one.
            publication = PublicationEvidence.Dated(date, work.Precision ?? DatePrecision.Year, work.Basis ?? DateBasis.Unknown);
        }
        else
        {
            publication = work.Status == PublicationStatus.Forthcoming ? PublicationEvidence.KnownUndated() : PublicationEvidence.NotStated;
        }

        return new CatalogAnswerEntry
        {
            SavedWorkId = work.WorkId,
            Title = work.Title,
            Credits = work.Credits.Select(credit => new CatalogEntryCredit(credit.Ordinal, credit.Name, credit.Role, null, credit.AuthorId)).ToArray(),
            Positions = positions.ToArray(),
            Class = ClassOf(work.VersionClass),
            Publication = publication,
            LegacyPrecision = legacy,
            SeriesLabel = seriesLabel
        };
    }

    /// <summary>A saved position is the selected claim in an ordering nobody stated: it stays unspecified, never publication order.</summary>
    public static PositionClaim[] SavedPositions(SavedSeriesEntryEvidence entry) =>
        entry.PositionSort is null && string.IsNullOrWhiteSpace(entry.PositionLabel)
            ? []
            : [new PositionClaim(entry.PositionLabel, entry.PositionSort)];
}
