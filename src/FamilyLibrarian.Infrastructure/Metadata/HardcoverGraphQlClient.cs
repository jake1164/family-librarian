using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyLibrarian.Application.Catalog.Discovery;

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
    /// <summary>Carries a discovery request's server-owned budget through the handler pipeline.</summary>
    internal static readonly HttpRequestOptionsKey<IDiscoveryBudget> DiscoveryBudgetKey = new("FamilyLibrarian.DiscoveryBudget");

    private const string CapacityCode = "request_exceeds_capacity";

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

    /// <summary>
    /// The discovery variant of <see cref="ExecuteAsync{TData}"/>. It never throws for a provider condition: every transport
    /// or GraphQL failure becomes a typed <see cref="HardcoverDiscoveryResponse{TData}"/>, and a response carrying both data
    /// and errors keeps its data with each errored path listed, so a caller can keep the valid items and never read a failed
    /// field as absent. Caller cancellation still propagates. The existing strict contract is untouched.
    /// </summary>
    public static async Task<HardcoverDiscoveryResponse<TData>> ExecuteDiscoveryAsync<TData>(
        HttpClient httpClient,
        string query,
        object? variables,
        IDiscoveryBudget budget,
        CancellationToken cancellationToken)
        where TData : class
    {
        var remaining = budget.RemainingWork;
        if (remaining <= TimeSpan.Zero)
        {
            return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.TimedOut, DiscoveryCodes.DeadlineReached);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        using var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = JsonContent.Create(new HardcoverGraphQlRequest(query, variables))
        };
        request.Options.Set(DiscoveryBudgetKey, budget);

        try
        {
            using var response = await httpClient.SendAsync(request, deadline.Token);
            return await ReadDiscoveryAsync<TData>(response, deadline.Token);
        }
        catch (DiscoveryTransportRefusedException refused)
        {
            return refused.Admission switch
            {
                AttemptAdmission.DeadlineExceeded => HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.TimedOut, DiscoveryCodes.DeadlineReached),
                AttemptAdmission.JourneyExpired => HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.Refused, DiscoveryCodes.JourneyExpired),
                null => HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.Throttled, refused.Code),
                _ => HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.Refused, DiscoveryCodes.AttemptCeiling)
            };
        }
        catch (InvalidOperationException ex) when (ex.Message == HardcoverAuthorizationHandler.MissingTokenMessage)
        {
            return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.SetupMissing, DiscoveryCodes.SetupMissing);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The provider-work deadline, or the client's own timeout, fired; the caller did not cancel.
            return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.TimedOut, DiscoveryCodes.DeadlineReached);
        }
        catch (HttpRequestException)
        {
            return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.Failed, DiscoveryCodes.ProviderError);
        }
    }

    private static async Task<HardcoverDiscoveryResponse<TData>> ReadDiscoveryAsync<TData>(
        HttpResponseMessage response, CancellationToken cancellationToken)
        where TData : class
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.Throttled, DiscoveryCodes.RetryAfterTooLong);
            case HttpStatusCode.Unauthorized:
                return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.AuthorizationDenied, DiscoveryCodes.AuthorizationDenied);
            case HttpStatusCode.Forbidden:
                // Documented: 403 request_exceeds_capacity means the query asks for more top-level fields than the bucket
                // holds. Delaying the identical query cannot help; any other 403 is an authorization or scope failure.
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return body.Contains(CapacityCode, StringComparison.Ordinal)
                    ? HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.QueryCapacity, DiscoveryCodes.QueryCapacity)
                    : HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.AuthorizationDenied, DiscoveryCodes.AuthorizationDenied);
        }

        if (!response.IsSuccessStatusCode)
        {
            return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.Failed, DiscoveryCodes.ProviderError);
        }

        HardcoverGraphQlResponse<TData>? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<HardcoverGraphQlResponse<TData>>(cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.Failed, DiscoveryCodes.ProviderError);
        }

        // No data at all is a fatal validation/authorization failure, never a verified-empty answer.
        if (envelope?.Data is null)
        {
            return HardcoverDiscoveryResponse<TData>.Stopped(HardcoverTransport.Failed, DiscoveryCodes.ProviderError);
        }

        var errors = (envelope.Errors ?? [])
            .Select(error => new HardcoverFieldError(PathOf(error.Path), error.Extensions?.Code))
            .ToArray();
        return new HardcoverDiscoveryResponse<TData>(envelope.Data, errors, HardcoverTransport.Ok, null);
    }

    private static string PathOf(JsonElement? path)
    {
        if (path is not { ValueKind: JsonValueKind.Array } elements)
        {
            return string.Empty;
        }

        return string.Join('.', elements.EnumerateArray().Select(element => element.ToString()));
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

        [JsonPropertyName("path")]
        public JsonElement? Path { get; init; }

        [JsonPropertyName("extensions")]
        public HardcoverGraphQlErrorExtensions? Extensions { get; init; }
    }

    private sealed class HardcoverGraphQlErrorExtensions
    {
        [JsonPropertyName("code")]
        public string? Code { get; init; }
    }
}

internal enum HardcoverTransport
{
    Ok = 0,
    Refused = 1,
    Throttled = 2,
    QueryCapacity = 3,
    AuthorizationDenied = 4,
    SetupMissing = 5,
    TimedOut = 6,
    Failed = 7
}

/// <summary>A GraphQL error's data path (for example <c>series.0.book_series.3.book</c>); the message text is deliberately not kept.</summary>
internal sealed record HardcoverFieldError(string Path, string? Code);

/// <summary>
/// The field errors of one response, attributed to the rows of one list by their data path (for example
/// <c>series.0.book_series.3.book</c> is row 3 of <c>series.0.book_series</c>). A null caused by an error is unknown, not
/// absent, so a row with any error at or beneath it is never trusted as a complete item. An error that cannot be placed on a
/// row of the list is <see cref="Unattributed"/>: it makes the page partial but names no row.
/// </summary>
internal sealed class HardcoverRowErrors
{
    private readonly Dictionary<int, HashSet<string>> rows = [];

    private HardcoverRowErrors()
    {
    }

    public bool Unattributed { get; private set; }

    public static HardcoverRowErrors Of(IReadOnlyList<HardcoverFieldError> errors, string listPath)
    {
        var result = new HardcoverRowErrors();
        var prefix = listPath + ".";
        foreach (var error in errors)
        {
            if (!error.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Unattributed = true;
                continue;
            }

            var rest = error.Path.AsSpan(prefix.Length);
            var dot = rest.IndexOf('.');
            if (!int.TryParse(dot < 0 ? rest : rest[..dot], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var row))
            {
                result.Unattributed = true;
                continue;
            }

            var field = string.Empty;
            if (dot >= 0)
            {
                var below = rest[(dot + 1)..];
                var next = below.IndexOf('.');
                field = (next < 0 ? below : below[..next]).ToString();
            }

            if (!result.rows.TryGetValue(row, out var fields))
            {
                result.rows[row] = fields = new HashSet<string>(StringComparer.Ordinal);
            }

            fields.Add(field);
        }

        return result;
    }

    /// <summary>True when anything at or beneath the row failed.</summary>
    public bool Failed(int row) => rows.ContainsKey(row);

    /// <summary>True when the row itself, or the named field directly on it, failed.</summary>
    public bool FieldFailed(int row, string field) =>
        rows.TryGetValue(row, out var fields) && (fields.Contains(string.Empty) || fields.Contains(field));
}

internal sealed record HardcoverDiscoveryResponse<TData>(
    TData? Data,
    IReadOnlyList<HardcoverFieldError> Errors,
    HardcoverTransport Transport,
    string? Code)
    where TData : class
{
    public bool HasData => Transport == HardcoverTransport.Ok && Data is not null;

    public static HardcoverDiscoveryResponse<TData> Stopped(HardcoverTransport transport, string code) =>
        new(null, [], transport, code);
}
