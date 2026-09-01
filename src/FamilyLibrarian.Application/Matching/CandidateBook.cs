namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// A destination catalog entry normalized to the fields matching cares about,
/// regardless of whether it came from a CWA OPDS Atom entry or an
/// Audiobookshelf library item.
/// </summary>
public sealed record CandidateBook(string ExternalId, string Title, string? Author);
