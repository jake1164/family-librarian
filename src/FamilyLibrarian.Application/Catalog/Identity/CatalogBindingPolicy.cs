using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Identity;

public enum BindingAction
{
    /// <summary>Use the one existing row.</summary>
    BindExisting,

    /// <summary>Create a new row for this credit or series.</summary>
    CreateNew,

    /// <summary>A series that cannot be bound to any one row: retained as an unbound claim, no entry created.</summary>
    LeaveUnbound
}

/// <param name="ExistingId">The Author or Series the credit binds to, when <see cref="Action"/> is <see cref="BindingAction.BindExisting"/>.</param>
/// <param name="SourceIdConflict">More than one row carries the same source id: a data conflict to report, never to resolve by choosing one.</param>
public sealed record AuthorBindingDecision(BindingAction Action, AuthorBindingBasis Basis, Guid? ExistingId, bool SourceIdConflict);

public sealed record SeriesBindingDecision(BindingAction Action, SeriesBindingBasis? Basis, Guid? ExistingId, bool SourceIdConflict);

/// <param name="Id">The existing row.</param>
/// <param name="ReferencesDifferentSourceId">The row is already tied to a different id from the same provider, so it is a different person or series.</param>
public sealed record NameMatch(Guid Id, bool ReferencesDifferentSourceId);

/// <summary>
/// How a credited name or series label binds to existing rows. A name alone never proves identity, and a new relationship is never
/// attached to an existing person or series merely because it sorts first or was created first.
/// </summary>
public static class CatalogBindingPolicy
{
    /// <param name="nameMatches">Every Author with the credit's normalized name, with whether each is tied to a different source id.</param>
    /// <param name="referenceMatches">Every Author tied to exactly the credit's source id; empty when the credit carries none.</param>
    /// <param name="hasSourceId">Whether the credit carries the provider's own author id.</param>
    public static AuthorBindingDecision DecideAuthor(IReadOnlyList<NameMatch> nameMatches, IReadOnlyList<Guid> referenceMatches, bool hasSourceId)
    {
        ArgumentNullException.ThrowIfNull(nameMatches);
        ArgumentNullException.ThrowIfNull(referenceMatches);

        if (hasSourceId)
        {
            if (referenceMatches.Count == 1)
            {
                return new AuthorBindingDecision(BindingAction.BindExisting, AuthorBindingBasis.Reference, referenceMatches[0], false);
            }

            if (referenceMatches.Count > 1)
            {
                // The same source id on several rows cannot select one; the credit gets its own provisional row and is reported.
                return new AuthorBindingDecision(BindingAction.CreateNew, AuthorBindingBasis.Provisional, null, true);
            }
        }

        var candidates = (hasSourceId ? nameMatches.Where(match => !match.ReferencesDifferentSourceId) : nameMatches).ToArray();
        return candidates.Length switch
        {
            0 => new AuthorBindingDecision(BindingAction.CreateNew, AuthorBindingBasis.Created, null, false),
            1 => new AuthorBindingDecision(BindingAction.BindExisting, AuthorBindingBasis.Name, candidates[0].Id, false),
            _ => new AuthorBindingDecision(BindingAction.CreateNew, AuthorBindingBasis.Provisional, null, false)
        };
    }

    public static SeriesBindingDecision DecideSeries(IReadOnlyList<NameMatch> nameMatches, IReadOnlyList<Guid> referenceMatches, bool hasSourceId)
    {
        ArgumentNullException.ThrowIfNull(nameMatches);
        ArgumentNullException.ThrowIfNull(referenceMatches);

        if (hasSourceId)
        {
            if (referenceMatches.Count == 1)
            {
                return new SeriesBindingDecision(BindingAction.BindExisting, SeriesBindingBasis.Reference, referenceMatches[0], false);
            }

            if (referenceMatches.Count > 1)
            {
                return new SeriesBindingDecision(BindingAction.LeaveUnbound, null, null, true);
            }
        }

        var candidates = (hasSourceId ? nameMatches.Where(match => !match.ReferencesDifferentSourceId) : nameMatches).ToArray();
        return candidates.Length switch
        {
            0 => new SeriesBindingDecision(BindingAction.CreateNew, SeriesBindingBasis.Created, null, false),
            1 => new SeriesBindingDecision(BindingAction.BindExisting, SeriesBindingBasis.Name, candidates[0].Id, false),
            _ => new SeriesBindingDecision(BindingAction.LeaveUnbound, null, null, false)
        };
    }
}
