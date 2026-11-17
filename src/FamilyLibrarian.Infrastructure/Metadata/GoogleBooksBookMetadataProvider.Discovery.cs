using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Providers;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Read-only series discovery for Google Books, on the same provider instance and credential pipeline as search/detail.
/// </summary>
/// <remarks>
/// <para>
/// <b>What Google offers, and what is used.</b> Google's public Discovery document (revision 20261110, read 2026-11-17) declares
/// <c>volumes.get</c> parameter <c>includeNonComicsSeries</c> (boolean, default false) and
/// <c>Volume.volumeInfo.seriesInfo</c> with <c>bookDisplayNumber</c> (display only), <c>shortSeriesBookTitle</c> (a title of the
/// <em>book</em> in the series context) and <c>volumeSeries[]</c> of <c>seriesId</c>, <c>orderNumber</c>, <c>seriesBookType</c>
/// and <c>issue[]</c>. That is the whole surface this adapter reads: one <c>volumes.get</c> of one known volume. Nothing in it
/// names the series, so a series from Google is a native id with an empty label, never a name.
/// </para>
/// <para>
/// <b>Identity.</b> A Google volume is an <em>edition</em> (a specific publication), so the subject must be an Edition
/// reference; Google has no Work entity and none is invented. A <c>seriesId</c> is Google's own id for a series and becomes a
/// native series reference; an entry with no usable id is dropped, because without a name or an id nothing identifies it.
/// <c>orderNumber</c> is the position claim's sort value and the book's number in that series; <c>bookDisplayNumber</c> is a
/// single volume-level string, so it is used as the label only when exactly one series entry exists. Which ordering the number
/// states (publication, reading) is not stated, so the ordering kind stays unknown.
/// </para>
/// <para>
/// <b>Not used.</b> <c>seriesBookType</c> is not mapped to a role or class: a sampled novel reported <c>ISSUE</c>, so the value
/// follows comics terminology and says nothing reliable about a novel. <c>issue[]</c> (omnibus and collection contents) is not
/// mapped and is reported as such. The two series endpoints (<c>series.get</c>, <c>series.membership.get</c>) are not called:
/// the tested key was answered HTTP 401 on 2026-11-16, the generated schema lists a Books OAuth scope for them, and this
/// integration adds no OAuth. That is a scoped observation about one key, not proof they are closed to every key. Google has no
/// author entity and no author-to-series query, so author operations are unsupported rather than approximated by a name search.
/// </para>
/// <para>
/// <b>Evidence status.</b> The shapes above are schema-declared and match two responses the comparison lab captured on 2026-11-16
/// with its own key (a positive Fourth Wing volume and a Harry Potter volume that carried no <c>seriesInfo</c>), so coverage is
/// uneven and unproven. No request was made to Google for this implementation, so the operation is declared
/// <see cref="CapabilityState.Unverified"/> and an empty answer only ever means "this volume response stated no series".
/// </para>
/// <para>
/// <b>Budget.</b> Every physical attempt is reserved from the journey budget by <see cref="GoogleBooksTransientRetryHandler"/>
/// before it is dispatched; the handler's own two-retry policy does not apply to a request that carries a budget, so the
/// budget's single retry and attempt ceilings decide. A 429 is reported and never retried. A <c>Retry-After</c> that does not fit
/// the remaining time ends the operation instead of retrying early. The API key is appended by the existing handler and never
/// appears in a result, a note or a scope; only stable codes leave this class.
/// </para>
/// </remarks>
public sealed partial class GoogleBooksBookMetadataProvider
{
    private const string ProviderKey = ProviderRegistry.GoogleBooksProviderId;
    private const int MaximumBodyBytes = 2 * 1024 * 1024;
    private const int MaximumDisplayNumberLength = 64;

    // Local codes of the shared stable-code shape; the shared list is not extended for one adapter.
    private const string SubjectNotFound = "subject-not-found";
    private const string NoSeriesInfo = "no-series-info-in-volume";
    private const string SeriesInfoWithoutSeries = "series-info-without-series";
    private const string SeriesNameNotProvided = "series-name-not-provided";
    private const string SeriesOrderUnreadable = "series-order-unreadable";
    private const string SeriesPositionConflict = "series-position-conflict";
    private const string SeriesIssuesNotMapped = "series-issues-not-mapped";
    private const string BookAnchorMismatch = "book-anchor-mismatch";
    private const string ResponseTooLarge = "response-too-large";
    private const string VolumeSeriesFilter = "volume-series-info-include-non-comics";

    private static readonly DiscoveryCapabilities DiscoveryCapabilityTable = new(
    [
        new(DiscoveryOperation.FindSeriesForBook, DiscoveryCapability.Unverified("live-probe-not-run-coverage-uneven")),
        new(DiscoveryOperation.GetSeriesMembers, DiscoveryCapability.Unsupported("series-endpoints-not-key-accessible")),
        new(DiscoveryOperation.ResolveAuthor, DiscoveryCapability.Unsupported("no-native-author-entity")),
        new(DiscoveryOperation.GetAuthorWorks, DiscoveryCapability.Unsupported("no-author-id-query")),
        new(DiscoveryOperation.GetAuthorSeries, DiscoveryCapability.Unsupported("no-author-to-series-query"))
    ]);

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    string IBookMetadataDiscoveryProvider.ProviderId => Id;

    DiscoveryCapabilities IBookMetadataDiscoveryProvider.Capabilities => DiscoveryCapabilityTable;

    // ------------------------------------------------------------------------------------------------ book -> series

    /// <summary>The series-only view of <see cref="FindSeriesClaimsForBookAsync"/>: the same single read, projected, never fetched twice.</summary>
    public async Task<DiscoveryResult<SeriesRef>> FindSeriesForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
        DiscoveryResultProjection.SeriesOnly(await FindSeriesClaimsForBookAsync(book, budget, cancellationToken));

    /// <summary>
    /// One <c>volumes.get</c> for one known volume, returning each series claim <em>with</em> this volume's position in it:
    /// native Google series ids (label empty, because Google sends no name) and the <c>orderNumber</c> position claims.
    /// </summary>
    public async Task<DiscoveryResult<BookSeriesClaim>> FindSeriesClaimsForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(budget);
        var volume = book;
        var scope = new DiscoveryScope(DiscoveryOperation.FindSeriesForBook, volume, VolumeSeriesFilter);
        if (!string.Equals(volume.ProviderId, ProviderKey, StringComparison.Ordinal) ||
            volume.Kind != ExternalReferenceEntityType.Edition ||
            !IsValidExternalId(volume.ExternalId))
        {
            // A volume is an edition. A Work, another provider's id or a malformed id never reaches a URL.
            return Refused<BookSeriesClaim>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        using var work = budget.BeginWork();
        var reply = await GetAsync<GoogleSeriesVolume>(
            $"volumes/{Uri.EscapeDataString(volume.ExternalId)}?includeNonComicsSeries=true", budget, cancellationToken);
        if (!reply.HasData)
        {
            return Stopped<BookSeriesClaim>(scope, budget, reply.Transport, reply.Code);
        }

        var data = reply.Data!;
        if (!string.Equals(data.Id, volume.ExternalId, StringComparison.Ordinal))
        {
            // A response for a different volume is never read as this volume's claims.
            return NoUsable<BookSeriesClaim>(scope, budget, [BookAnchorMismatch]);
        }

        var info = data.VolumeInfo?.SeriesInfo;
        if (info is null)
        {
            return Empty<BookSeriesClaim>(scope, budget, [NoSeriesInfo]);
        }

        if (info.VolumeSeries is not { Count: > 0 } entries)
        {
            return Empty<BookSeriesClaim>(scope, budget, [SeriesInfoWithoutSeries]);
        }

        var notes = new List<string>();
        var display = DisplayNumber(info.BookDisplayNumber);
        var claims = new List<BookSeriesClaim>();
        var seen = new HashSet<(string Id, int? Order)>();
        foreach (var entry in entries)
        {
            if (entry is null || !IsValidExternalId(entry.SeriesId) ||
                !SourceRef.TryCreate(ProviderKey, entry.SeriesId, ExternalReferenceEntityType.Series, out var seriesId))
            {
                // Without a name or a usable id nothing identifies this entry.
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            var order = OrderOf(entry.OrderNumber, notes);
            if (!seen.Add((seriesId!.ExternalId, order)))
            {
                notes.Add(DiscoveryCodes.DuplicateDropped);
                continue;
            }

            if (entry.Issue.ValueKind == JsonValueKind.Array && entry.Issue.GetArrayLength() > 0)
            {
                notes.Add(SeriesIssuesNotMapped);
            }

            var label = entries.Count == 1 && display is not null ? display : order?.ToString(CultureInfo.InvariantCulture);
            var position = label is null && order is null
                ? null
                : new PositionClaim(label, order, null, seriesId);
            notes.Add(SeriesNameNotProvided);
            claims.Add(new BookSeriesClaim(new SeriesRef(string.Empty, seriesId), position));
        }

        if (claims.GroupBy(claim => claim.Series.Id).Any(group => group.Count() > 1))
        {
            // The same Google series with two different positions: both claims are kept, neither is chosen.
            notes.Add(SeriesPositionConflict);
        }

        if (claims.Count == 0)
        {
            return NoUsable<BookSeriesClaim>(scope, budget, Distinct(notes));
        }

        budget.RecordItems(claims.Count);
        return DiscoveryResult.Found(
            claims, scope, DiscoveryCompletion.ExhaustedInCheckedScope, time.GetUtcNow(), budget.Usage, notes: Distinct(notes));
    }

    // ---------------------------------------------------------------------------------------- series members, authors

    public Task<DiscoveryResult<SeriesMember>> GetSeriesMembersAsync(
        SeriesRef series, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(series);
        return Task.FromResult(Unsupported<SeriesMember>(
            new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, series.Id), DiscoveryOperation.GetSeriesMembers, budget));
    }

    public Task<DiscoveryResult<AuthorRef>> ResolveAuthorAsync(
        AuthorAnchor anchor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return Task.FromResult(Unsupported<AuthorRef>(
            new DiscoveryScope(DiscoveryOperation.ResolveAuthor, anchor.Id), DiscoveryOperation.ResolveAuthor, budget));
    }

    public Task<DiscoveryResult<AuthorWork>> GetAuthorWorksAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(author);
        return Task.FromResult(Unsupported<AuthorWork>(
            new DiscoveryScope(DiscoveryOperation.GetAuthorWorks, author.Id), DiscoveryOperation.GetAuthorWorks, budget));
    }

    public Task<DiscoveryResult<AuthorSeriesEntry>> GetAuthorSeriesAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(author);
        return Task.FromResult(Unsupported<AuthorSeriesEntry>(
            new DiscoveryScope(DiscoveryOperation.GetAuthorSeries, author.Id), DiscoveryOperation.GetAuthorSeries, budget));
    }

    // An unsupported operation makes no provider call and never answers Empty.
    private DiscoveryResult<T> Unsupported<T>(DiscoveryScope scope, DiscoveryOperation operation, IDiscoveryBudget budget) =>
        DiscoveryResult.NotAnswered<T>(
            DiscoveryOutcome.Unsupported, scope, time.GetUtcNow(), budget.Usage, [DiscoveryCapabilityTable.Get(operation).ReasonCode]);

    // ------------------------------------------------------------------------------------------------- mapping

    private static string? DisplayNumber(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        text = text?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > MaximumDisplayNumberLength ? null : text;
    }

    // Only a whole, non-negative number is a position. Anything else keeps the claim without a sort value and says so.
    private static int? OrderOf(JsonElement value, List<string> notes)
    {
        if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var order) && order >= 0)
        {
            return order;
        }

        notes.Add(SeriesOrderUnreadable);
        return null;
    }

    private static string[] Distinct(IEnumerable<string> notes) => notes.Distinct(StringComparer.Ordinal).ToArray();

    // ------------------------------------------------------------------------------------------------- transport

    private enum GoogleTransport
    {
        Ok,
        NotFound,
        Throttled,
        TimedOut,
        Unavailable,
        SetupMissing,
        Refused,
        Failed
    }

    private sealed record GoogleReply<T>(T? Data, GoogleTransport Transport, string? Code)
        where T : class
    {
        public bool HasData => Data is not null;

        public static GoogleReply<T> Stopped(GoogleTransport transport, string code) => new(null, transport, code);
    }

    // The retry handler reserves every physical attempt (including the one permitted retry) from the budget before it
    // dispatches, so this method does not reserve a second time. The whole request is bounded by the remaining provider-work time.
    private async Task<GoogleReply<T>> GetAsync<T>(string relativeUri, IDiscoveryBudget budget, CancellationToken cancellationToken)
        where T : class
    {
        var remaining = budget.RemainingWork;
        if (remaining <= TimeSpan.Zero)
        {
            return GoogleReply<T>.Stopped(GoogleTransport.TimedOut, DiscoveryCodes.DeadlineReached);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        request.Options.Set(GoogleBooksTransientRetryHandler.DiscoveryBudgetKey, budget);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return await ReadAsync<T>(response, budget, deadline.Token);
        }
        catch (DiscoveryTransportRefusedException refused)
        {
            return refused.Admission switch
            {
                AttemptAdmission.DeadlineExceeded => GoogleReply<T>.Stopped(GoogleTransport.TimedOut, DiscoveryCodes.DeadlineReached),
                AttemptAdmission.JourneyExpired => GoogleReply<T>.Stopped(GoogleTransport.Refused, DiscoveryCodes.JourneyExpired),
                _ => GoogleReply<T>.Stopped(GoogleTransport.Refused, DiscoveryCodes.AttemptCeiling)
            };
        }
        catch (InvalidOperationException ex) when (ex.Message == GoogleBooksApiKeyHandler.MissingKeyMessage)
        {
            return GoogleReply<T>.Stopped(GoogleTransport.SetupMissing, DiscoveryCodes.SetupMissing);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The provider-work deadline, or the client's own timeout, fired; the caller did not cancel.
            return GoogleReply<T>.Stopped(GoogleTransport.TimedOut, DiscoveryCodes.DeadlineReached);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Headers arrive first (ResponseHeadersRead), so a connection that ends or resets while the body streams surfaces
            // as an HttpIOException (an IOException). Only these network conditions become a typed failure; no exception text
            // travels (it could echo a request address), a programming error is not swallowed, and cancellation is handled above.
            return GoogleReply<T>.Stopped(GoogleTransport.Failed, DiscoveryCodes.ProviderError);
        }
    }

    private static async Task<GoogleReply<T>> ReadAsync<T>(
        HttpResponseMessage response, IDiscoveryBudget budget, CancellationToken cancellationToken)
        where T : class
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return GoogleReply<T>.Stopped(GoogleTransport.NotFound, SubjectNotFound);
            case HttpStatusCode.TooManyRequests:
                // Reported, never retried: a wait that does not fit the remaining work is said so, any other is "throttled".
                return GoogleReply<T>.Stopped(
                    GoogleTransport.Throttled,
                    GoogleBooksTransientRetryHandler.RetryAfterOf(response) is { } wait && wait > budget.RemainingWork
                        ? DiscoveryCodes.RetryAfterTooLong
                        : DiscoveryCodes.Throttled);
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                // Google also answers 403 for an exhausted quota; the body is not read to tell the two apart (unverified), so
                // this code can cover both and states only that the request was refused.
                return GoogleReply<T>.Stopped(GoogleTransport.Unavailable, DiscoveryCodes.AuthorizationDenied);
        }

        if (!response.IsSuccessStatusCode)
        {
            // A transient answer whose Retry-After did not fit (or whose retry the budget refused) is a throttle when the
            // provider named a wait that is too long, otherwise a plain failure.
            return (int)response.StatusCode >= 500 &&
                GoogleBooksTransientRetryHandler.RetryAfterOf(response) is { } wait && wait >= budget.RemainingWork
                    ? GoogleReply<T>.Stopped(GoogleTransport.Throttled, DiscoveryCodes.RetryAfterTooLong)
                    : GoogleReply<T>.Stopped(GoogleTransport.Failed, DiscoveryCodes.ProviderError);
        }

        if (response.Content.Headers.ContentLength is > MaximumBodyBytes)
        {
            return GoogleReply<T>.Stopped(GoogleTransport.Failed, ResponseTooLarge);
        }

        using var buffer = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaximumBodyBytes)
                {
                    return GoogleReply<T>.Stopped(GoogleTransport.Failed, ResponseTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }

        try
        {
            var data = JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            return data is null
                ? GoogleReply<T>.Stopped(GoogleTransport.Failed, DiscoveryCodes.ProviderError)
                : new GoogleReply<T>(data, GoogleTransport.Ok, null);
        }
        catch (JsonException)
        {
            return GoogleReply<T>.Stopped(GoogleTransport.Failed, DiscoveryCodes.ProviderError);
        }
    }

    // ------------------------------------------------------------------------------------------- result building

    private DiscoveryResult<T> Empty<T>(DiscoveryScope scope, IDiscoveryBudget budget, IReadOnlyList<string> notes) =>
        DiscoveryResult.Empty<T>(scope, time.GetUtcNow(), budget.Usage, notes);

    private DiscoveryResult<T> NoUsable<T>(DiscoveryScope scope, IDiscoveryBudget budget, IReadOnlyList<string> reasons) =>
        DiscoveryResult.NoUsableItems<T>(
            scope, DiscoveryCompletion.ExhaustedInCheckedScope, null, time.GetUtcNow(), budget.Usage, reasons);

    // A request refused before any provider call: nothing was dispatched, so usage is whatever the budget already holds.
    private DiscoveryResult<T> Refused<T>(DiscoveryScope scope, IDiscoveryBudget budget, string code) =>
        NotAnswered<T>(scope, budget, DiscoveryOutcome.Failed, code);

    private DiscoveryResult<T> NotAnswered<T>(DiscoveryScope scope, IDiscoveryBudget budget, DiscoveryOutcome outcome, string code) =>
        DiscoveryResult.NotAnswered<T>(outcome, scope, time.GetUtcNow(), budget.Usage, [code]);

    private DiscoveryResult<T> Stopped<T>(DiscoveryScope scope, IDiscoveryBudget budget, GoogleTransport transport, string? reason)
    {
        var (outcome, code) = transport switch
        {
            GoogleTransport.Throttled => (DiscoveryOutcome.Throttled, reason ?? DiscoveryCodes.Throttled),
            GoogleTransport.TimedOut => (DiscoveryOutcome.TimedOut, DiscoveryCodes.DeadlineReached),
            GoogleTransport.Unavailable => (DiscoveryOutcome.Unavailable, reason ?? DiscoveryCodes.AuthorizationDenied),
            GoogleTransport.SetupMissing => (DiscoveryOutcome.Unavailable, DiscoveryCodes.SetupMissing),
            _ => (DiscoveryOutcome.Failed, reason ?? DiscoveryCodes.ProviderError)
        };
        return NotAnswered<T>(scope, budget, outcome, code);
    }

    // ------------------------------------------------------------------------------------------------------ models

    private sealed class GoogleSeriesVolume
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("volumeInfo")]
        public GoogleSeriesVolumeInfo? VolumeInfo { get; init; }
    }

    private sealed class GoogleSeriesVolumeInfo
    {
        [JsonPropertyName("seriesInfo")]
        public GoogleSeriesInfo? SeriesInfo { get; init; }
    }

    private sealed class GoogleSeriesInfo
    {
        [JsonPropertyName("bookDisplayNumber")]
        public JsonElement BookDisplayNumber { get; init; }

        [JsonPropertyName("volumeSeries")]
        public IReadOnlyList<GoogleVolumeSeriesEntry?>? VolumeSeries { get; init; }
    }

    private sealed class GoogleVolumeSeriesEntry
    {
        [JsonPropertyName("seriesId")]
        public string? SeriesId { get; init; }

        [JsonPropertyName("orderNumber")]
        public JsonElement OrderNumber { get; init; }

        [JsonPropertyName("issue")]
        public JsonElement Issue { get; init; }
    }
}
