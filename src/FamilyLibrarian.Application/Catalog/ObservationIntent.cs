namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Why a provider record is being observed. The intents are distinct on purpose, not one permissive upsert: they differ in what
/// identifies the Work, how much of the record is validated, and what may be written.
/// </summary>
public abstract record ObservationIntent
{
    private protected ObservationIntent()
    {
    }
}

/// <summary>An ordinary explicit user resolve: a known reference is provider-free and write-free; an unknown one is matched by proof.</summary>
public sealed record ResolveIntent : ObservationIntent;

/// <summary>An administrator refresh of one stored reference: the reference names the Work only, and the fetched record is validated.</summary>
/// <param name="WorkId">The Work the stored reference belongs to.</param>
/// <param name="ProviderId">The provider of the stored reference.</param>
/// <param name="ExternalId">The external id of the stored reference.</param>
public sealed record RefreshIntent(Guid WorkId, string ProviderId, string ExternalId) : ObservationIntent;

/// <summary>An administrator-triggered cross-source search for one Work: attaches only a record whose ISBN proof names that Work.</summary>
/// <param name="WorkId">The Work being enriched.</param>
public sealed record DiscoverIntent(Guid WorkId) : ObservationIntent;
