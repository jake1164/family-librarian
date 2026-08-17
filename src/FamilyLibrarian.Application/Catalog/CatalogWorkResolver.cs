using System.Globalization;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

public sealed class CatalogWorkResolver(
    IEnumerable<IBookMetadataProvider> providers,
    ICatalogRepository catalogRepository,
    IClock clock)
{
    private readonly Dictionary<string, IBookMetadataProvider> _providers = providers
        .ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

    public async Task<CatalogWorkResolution> ResolveAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken)
    {
        ValidateProviderReference(providerId, externalId);

        var existingByReference = await catalogRepository.FindWorkByExternalReferenceAsync(
            providerId,
            externalId,
            cancellationToken);
        if (existingByReference is not null)
        {
            return new CatalogWorkResolution(existingByReference, false);
        }

        if (!_providers.TryGetValue(providerId, out var provider))
        {
            throw new UnknownMetadataProviderException(providerId);
        }

        var candidate = await provider.GetDetailsAsync(externalId, cancellationToken)
            ?? throw new CatalogCandidateNotFoundException(providerId, externalId);
        var observedAtUtc = clock.UtcNow;
        var isbn13s = candidate.Editions
            .Select(edition => edition.Isbn13)
            .Where(isbn13 => !string.IsNullOrWhiteSpace(isbn13))
            .Select(isbn13 => isbn13!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingByIsbn = isbn13s.Length == 0
            ? null
            : await catalogRepository.FindWorkByIsbn13Async(isbn13s, cancellationToken);

        if (existingByIsbn is not null)
        {
            catalogRepository.AddExternalReference(new ExternalReference(
                provider.Id,
                ExternalReferenceEntityType.Work,
                existingByIsbn.Id,
                candidate.ExternalId,
                observedAtUtc));
            await catalogRepository.SaveChangesAsync(cancellationToken);
            return new CatalogWorkResolution(existingByIsbn, false);
        }

        var work = new Work(
            candidate.Title,
            candidate.Description,
            candidate.CoverUrl,
            candidate.PublicationDate,
            candidate.PublicationDate is null ? PublicationStatus.Unknown : PublicationStatus.Published,
            observedAtUtc);

        await AddAuthorsAsync(work, candidate.Authors, observedAtUtc, cancellationToken);
        AddEditions(work, candidate.Editions, observedAtUtc);
        await AddSeriesEntriesAsync(work, candidate.Series, observedAtUtc, cancellationToken);
        catalogRepository.AddWork(work);
        catalogRepository.AddExternalReference(new ExternalReference(
            provider.Id,
            ExternalReferenceEntityType.Work,
            work.Id,
            candidate.ExternalId,
            observedAtUtc));
        await catalogRepository.SaveChangesAsync(cancellationToken);

        return new CatalogWorkResolution(work, true);
    }

    public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
        catalogRepository.GetWorkAsync(workId, cancellationToken);

    public Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(
        Guid workId,
        CancellationToken cancellationToken) =>
        catalogRepository.GetWorkSourcesAsync(workId, cancellationToken);

    private async Task AddAuthorsAsync(
        Work work,
        IReadOnlyList<string> names,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        foreach (var name in names
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Select(name => name.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Select((name, ordinal) => (name, ordinal)))
        {
            var normalizedName = CatalogText.NormalizeForMatch(name.name);
            var author = await catalogRepository.FindAuthorByNormalizedNameAsync(
                normalizedName,
                cancellationToken);
            if (author is null)
            {
                author = new Author(name.name, null, observedAtUtc);
                catalogRepository.AddAuthor(author);
            }

            work.AddAuthor(author, name.ordinal);
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

    private async Task AddSeriesEntriesAsync(
        Work work,
        IReadOnlyList<BookSeriesCandidate> candidates,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates.Where(candidate =>
                     !string.IsNullOrWhiteSpace(candidate.Name)))
        {
            var normalizedName = CatalogText.NormalizeForMatch(candidate.Name);
            var series = await catalogRepository.FindSeriesByNormalizedNameAsync(
                normalizedName,
                cancellationToken);
            if (series is null)
            {
                series = new Series(candidate.Name, SeriesStatus.Unknown, observedAtUtc);
                catalogRepository.AddSeries(series);
            }

            work.AddSeriesEntry(new SeriesEntry(
                series,
                work,
                candidate.PositionLabel,
                TryParsePosition(candidate.PositionLabel),
                candidate.IsPrimary,
                observedAtUtc));
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
}

public sealed record CatalogWorkResolution(Work Work, bool WasCreated);

public sealed class UnknownMetadataProviderException(string providerId)
    : InvalidOperationException($"The metadata provider '{providerId}' is not enabled.");

public sealed class CatalogCandidateNotFoundException(string providerId, string externalId)
    : InvalidOperationException($"The metadata candidate '{providerId}/{externalId}' was not found.");
