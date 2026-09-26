using System.Text.Json;

namespace FamilyLibrarian.Infrastructure.LibriVox;

/// <summary>Small live-search client for the public LibriVox audiobook catalog.</summary>
public sealed class LibriVoxApiClient(HttpClient httpClient, LibriVoxRequestThrottle throttle)
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;

    public async Task<IReadOnlyList<LibriVoxRecording>> SearchByTitleAsync(
        string title, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["title"] = title,
            ["format"] = "json",
            ["extended"] = "1",
            ["coverart"] = "1",
            ["limit"] = "50",
            ["offset"] = "0"
        };
        var uri = "api/feed/audiobooks/?" + string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await throttle.WaitForTurnAsync(cancellationToken);
            response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var retryDelay = RetryDelay(response);
            if (attempt == 1 || retryDelay is null) break;
            response.Dispose();
            response = null;
            await Task.Delay(retryDelay.Value, cancellationToken);
        }

        using (var completedResponse = response ?? throw new InvalidOperationException("The LibriVox request completed without an HTTP response."))
        {
            completedResponse.EnsureSuccessStatusCode();
            if (completedResponse.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                throw new InvalidDataException("The LibriVox catalog response exceeded the size limit.");
            }

            await using var stream = await completedResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[32 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes)
                {
                    throw new InvalidDataException("The LibriVox catalog response exceeded the size limit.");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            if (!TryGetPropertyIgnoreCase(document.RootElement, "books", out var books) || books.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The LibriVox catalog returned an unexpected response.");
            }

            return books.EnumerateArray().Select(ParseRecording).Where(recording => recording is not null)
                .Cast<LibriVoxRecording>().ToArray();
        }
    }

    private static TimeSpan? RetryDelay(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var supplied = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(5));
            return supplied >= TimeSpan.Zero && supplied <= TimeSpan.FromSeconds(30) ? supplied : null;
        }

        return (int)response.StatusCode >= 500 ? TimeSpan.FromSeconds(2) : null;
    }

    private static LibriVoxRecording? ParseRecording(JsonElement element)
    {
        var id = ReadString(element, "id");
        var title = ReadString(element, "title");
        var zip = ReadString(element, "url_zip_file");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 10 || id.Any(character => !char.IsAsciiDigit(character)) ||
            string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(zip, UriKind.Absolute, out var zipUri) || !IsTrustedMediaUri(zipUri))
        {
            return null;
        }

        var authors = ReadNames(element, "authors");
        var readers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<string>();
        if (TryGetPropertyIgnoreCase(element, "sections", out var sectionArray) && sectionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in sectionArray.EnumerateArray())
            {
                var sectionTitle = ReadString(section, "title");
                if (!string.IsNullOrWhiteSpace(sectionTitle)) sections.Add(sectionTitle);
                foreach (var reader in ReadReaderNames(section))
                {
                    if (reader.Any(character => !char.IsAsciiDigit(character))) readers.Add(reader);
                }
            }
        }
        var genres = ReadNames(element, "genres");
        var cover = ReadString(element, "coverart_thumbnail") ?? ReadString(element, "coverart_jpg");

        return new LibriVoxRecording(
            id, title, authors, ReadString(element, "language"), ReadInt(element, "copyright_year"),
            ReadBoundedInt(element, "num_sections", 0, 10_000) ?? sections.Count,
            ReadBoundedLong(element, "totaltimesecs", 0, 31_536_000), ReadString(element, "url_librivox") ?? ReadString(element, "url_project"),
            zipUri, sections, readers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            genres, SafeHttpsUri(cover));
    }

    private static string[] ReadNames(JsonElement element, string property)
    {
        if (!TryGetPropertyIgnoreCase(element, property, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Select(item =>
        {
            if (item.ValueKind == JsonValueKind.String) return item.GetString() ?? string.Empty;
            var name = ReadString(item, "name");
            if (name is not null) return name;
            var first = ReadString(item, "first_name");
            var last = ReadString(item, "last_name");
            return string.Join(' ', new[] { first, last }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Section readers use a distinct schema from authors/genres
    /// (<c>reader_id</c>/<c>display_name</c>, not <c>first_name</c>/<c>last_name</c>),
    /// so they get their own parser rather than folding into <see cref="ReadNames"/>.
    /// </summary>
    private static string[] ReadReaderNames(JsonElement section)
    {
        if (!TryGetPropertyIgnoreCase(section, "readers", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : ReadString(item, "display_name") ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ReadString(JsonElement element, string property) =>
        TryGetPropertyIgnoreCase(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;

    private static int? ReadInt(JsonElement element, string property) =>
        TryGetPropertyIgnoreCase(element, property, out var value) && int.TryParse(value.ToString(), out var parsed) ? parsed : null;

    private static long? ReadLong(JsonElement element, string property) =>
        TryGetPropertyIgnoreCase(element, property, out var value) && long.TryParse(value.ToString(), out var parsed) ? parsed : null;

    private static int? ReadBoundedInt(JsonElement element, string property, int minimum, int maximum) =>
        ReadInt(element, property) is { } value && value >= minimum && value <= maximum ? value : null;

    private static long? ReadBoundedLong(JsonElement element, string property, long minimum, long maximum) =>
        ReadLong(element, property) is { } value && value >= minimum && value <= maximum ? value : null;

    private static Uri? SafeHttpsUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    private static bool IsTrustedMediaUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.IdnHost.Equals("archive.org", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.Equals("librivox.org", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".librivox.org", StringComparison.OrdinalIgnoreCase));

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}

/// <summary>Spaces LibriVox catalog calls across all in-process clients.</summary>
public sealed class LibriVoxRequestThrottle : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequestAtUtc;

    public async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequestAtUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            nextRequestAtUtc = DateTimeOffset.UtcNow.AddSeconds(3);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}

public sealed record LibriVoxRecording(
    string Id, string Title, IReadOnlyList<string> Authors, string? Language, int? PublicationYear,
    int? SectionCount, long? DurationSeconds, string? ProjectUrl, Uri ZipUri,
    IReadOnlyList<string> SectionTitles, IReadOnlyList<string> Readers, IReadOnlyList<string> Genres, Uri? CoverUri);
