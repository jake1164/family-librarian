using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Posts one GraphQL operation to Hardcover's API and unwraps its envelope.
/// </summary>
/// <remarks>
/// Shared by <see cref="HardcoverBookMetadataProvider"/> and
/// <see cref="HardcoverLanguageLookupCache"/> so the request/response envelope
/// shape (and how a GraphQL-level error is surfaced) is defined exactly once.
/// A non-2xx status (401/403/429/500/503 per Hardcover's documented response
/// codes) is left to <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>
/// to turn into an <see cref="HttpRequestException"/>, matching how every
/// other metadata provider in this codebase surfaces a transport failure.
/// </remarks>
internal static class HardcoverGraphQlClient
{
    public static async Task<TData?> ExecuteAsync<TData>(
        HttpClient httpClient,
        string query,
        object? variables,
        CancellationToken cancellationToken)
        where TData : class
    {
        using var response = await httpClient.PostAsJsonAsync(
            string.Empty,
            new HardcoverGraphQlRequest(query, variables),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<HardcoverGraphQlResponse<TData>>(
            cancellationToken: cancellationToken);

        if (envelope?.Errors is { Count: > 0 } errors)
        {
            throw new HttpRequestException(
                $"Hardcover GraphQL error: {string.Join("; ", errors.Select(error => error.Message))}");
        }

        return envelope?.Data;
    }

    private sealed record HardcoverGraphQlRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("variables")] object? Variables);

    private sealed class HardcoverGraphQlResponse<TData>
        where TData : class
    {
        [JsonPropertyName("data")]
        public TData? Data { get; init; }

        [JsonPropertyName("errors")]
        public IReadOnlyList<HardcoverGraphQlError>? Errors { get; init; }
    }

    private sealed class HardcoverGraphQlError
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
