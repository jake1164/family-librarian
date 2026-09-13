namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// A destination catalog entry normalized to the fields matching cares about,
/// regardless of whether it came from a CWA OPDS Atom entry or an
/// Audiobookshelf library item.
/// </summary>
/// <param name="Language">
/// Whatever language signal the source already exposes (e.g. a CWA OPDS
/// Dublin Core language element, or Audiobookshelf's metadata.language),
/// unnormalized. Null when the source has no language metadata at all --
/// see <see cref="LanguageAcceptance"/> for how that is treated.
/// </param>
public sealed record CandidateBook(string ExternalId, string Title, string? Author, string? Language = null);
