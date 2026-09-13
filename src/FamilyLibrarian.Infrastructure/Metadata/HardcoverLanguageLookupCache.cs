using System.Text.Json.Serialization;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Resolves Hardcover's numeric <c>language_id</c> foreign key to an ISO
/// 639-1 (two-letter) code.
/// </summary>
/// <remarks>
/// Hardcover's language lookup table changes essentially never, so this loads
/// it once per process lifetime rather than spending a request against a
/// fixed 5,000/day quota on every search result that carries an edition. That
/// requires this type to be a true singleton, which rules out the typed-client
/// convenience method (<c>AddHttpClient&lt;TClient&gt;()</c> always registers
/// its client type as transient) — so this takes <see cref="IHttpClientFactory"/>
/// and creates a client from the named registration only when actually
/// loading. A failed load is not cached, so a later call retries rather than
/// being poisoned by a transient failure for the rest of the process lifetime.
/// </remarks>
public sealed class HardcoverLanguageLookupCache(IHttpClientFactory httpClientFactory) : IDisposable
{
    public const string HttpClientName = "Hardcover.Languages";

    private const string LanguagesQuery = "query { languages { id code2 } }";

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private Dictionary<int, string>? _codesById;

    public void Dispose() => _mutex.Dispose();

    public async Task<string?> GetIsoCodeAsync(int? languageId, CancellationToken cancellationToken)
    {
        if (languageId is null)
        {
            return null;
        }

        var codes = await EnsureLoadedAsync(cancellationToken);
        return codes.TryGetValue(languageId.Value, out var code) ? code : null;
    }

    private async Task<Dictionary<int, string>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_codesById is { } loaded)
        {
            return loaded;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (_codesById is { } loadedAfterWait)
            {
                return loadedAfterWait;
            }

            using var client = httpClientFactory.CreateClient(HttpClientName);
            var data = await HardcoverGraphQlClient.ExecuteAsync<LanguagesData>(
                client,
                LanguagesQuery,
                variables: null,
                cancellationToken);

            var codesById = (data?.Languages ?? [])
                .Where(language => !string.IsNullOrWhiteSpace(language.Code2))
                .ToDictionary(language => language.Id, language => language.Code2!.ToLowerInvariant());

            _codesById = codesById;
            return codesById;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private sealed class LanguagesData
    {
        [JsonPropertyName("languages")]
        public IReadOnlyList<LanguageRow>? Languages { get; init; }
    }

    private sealed class LanguageRow
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("code2")]
        public string? Code2 { get; init; }
    }
}
