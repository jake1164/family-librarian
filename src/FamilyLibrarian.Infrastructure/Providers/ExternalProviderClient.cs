using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Providers;

/// <summary>Speaks the versioned external-provider HTTP protocol described in the M13 plan.</summary>
public sealed class ExternalProviderClient(IHttpClientFactory httpClientFactory) : IExternalProviderClient
{
    private static readonly TimeSpan AcquirePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(90);

    // Governs the manifest/search/job-status calls below, all of which are
    // small JSON and read fully into memory via ReadAsStringAsync — the
    // default is 2 GB. Has no effect on the artifact download in
    // AcquireAsync, which reads with HttpCompletionOption.ResponseHeadersRead
    // and never buffers into this limit regardless. See F9 in the
    // architecture review: an admin-registered external provider is still
    // third-party code, and only the download path already had a size bound.
    private const long MaxJsonResponseBytes = 10 * 1024 * 1024;

    public async Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        using var response = await client.GetAsync("manifest", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new HttpRequestException("The manifest response was not valid JSON.");

        var protocolVersions = json["protocolVersions"]?.AsArray()
            .Select(node => node?.GetValue<string>() ?? string.Empty)
            .Where(version => version.Length > 0)
            .ToArray();
        var protocolVersion = json["protocolVersion"]?.GetValue<string>()
            ?? protocolVersions?.FirstOrDefault()
            ?? "1";
        // A v1 manifest declares neither field — treat it as speaking only v1.
        protocolVersions ??= [protocolVersion];

        return new ExternalProviderManifest(
            protocolVersions,
            protocolVersion,
            json["instanceId"]?.GetValue<string>(),
            json["id"]?.GetValue<string>() ?? string.Empty,
            json["name"]?.GetValue<string>() ?? string.Empty,
            json["version"]?.GetValue<string>() ?? string.Empty,
            ParseCapabilities(json["capabilities"]),
            json["outputRetentionSeconds"]?.GetValue<int?>(),
            json["managementUrl"]?.GetValue<string>(),
            json["documentationUrl"]?.GetValue<string>(),
            json["egressPolicy"]?.GetValue<string>() ?? "NORMAL");
    }

    /// <summary>
    /// Accepts both the v2 structured object and the v1 flat capability-string
    /// array, per §4's tolerance note — a legacy array is parsed as
    /// best-effort <c>mediaTypes</c>/<c>operations</c> (ebook/audiobook go to
    /// media types, search/acquire go to operations, anything else is
    /// dropped rather than guessed at).
    /// </summary>
    private static ProviderCapabilities ParseCapabilities(JsonNode? node)
    {
        if (node is null)
        {
            return ProviderCapabilities.Empty;
        }

        if (node is JsonArray legacyArray)
        {
            var values = legacyArray.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray();
            var mediaTypes = values.Where(value => value is "ebook" or "audiobook").ToArray();
            var operations = values.Where(value => value is "search" or "acquire").ToArray();
            return new ProviderCapabilities(mediaTypes, operations, []);
        }

        return new ProviderCapabilities(
            node["mediaTypes"]?.AsArray().Select(item => item?.GetValue<string>() ?? string.Empty).ToArray() ?? [],
            node["operations"]?.AsArray().Select(item => item?.GetValue<string>() ?? string.Empty).ToArray() ?? [],
            node["features"]?.AsArray().Select(item => item?.GetValue<string>() ?? string.Empty).ToArray() ?? []);
    }

    public async Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        try
        {
            using var response = await client.GetAsync("health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Per §5: a non-2xx (or a connection failure, below) means
                // Family Librarian could not obtain usable health
                // information at all — distinct from a deliberately
                // reported degraded/unhealthy body.
                return ExternalProviderHealth.Unreachable;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                // Bare 2xx with no body is the v1 shape — treat as healthy.
                return new ExternalProviderHealth(
                    ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available);
            }

            var json = JsonNode.Parse(body);
            var status = ParseHealthStatus(json?["status"]?.GetValue<string>());
            var inherited = status switch
            {
                ProviderHealthStatus.Healthy => ProviderOperationalStatus.Available,
                ProviderHealthStatus.Degraded => ProviderOperationalStatus.Degraded,
                _ => ProviderOperationalStatus.Unavailable
            };

            return new ExternalProviderHealth(
                status,
                ParseOperationalStatus(json?["operations"]?["search"]?.GetValue<string>(), inherited),
                ParseOperationalStatus(json?["operations"]?["acquire"]?.GetValue<string>(), inherited));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // A timeout throws TaskCanceledException, not HttpRequestException —
            // this method's whole point is to never let a connectivity problem
            // escape as an exception, so both must degrade to Unreachable the
            // same way a non-2xx response already does above.
            return ExternalProviderHealth.Unreachable;
        }
    }

    private static ProviderHealthStatus ParseHealthStatus(string? value) => value?.ToLowerInvariant() switch
    {
        "degraded" => ProviderHealthStatus.Degraded,
        "unhealthy" => ProviderHealthStatus.Unhealthy,
        _ => ProviderHealthStatus.Healthy
    };

    private static ProviderOperationalStatus ParseOperationalStatus(string? value, ProviderOperationalStatus fallback) =>
        value?.ToLowerInvariant() switch
        {
            "available" => ProviderOperationalStatus.Available,
            "degraded" => ProviderOperationalStatus.Degraded,
            "unavailable" => ProviderOperationalStatus.Unavailable,
            _ => fallback
        };

    public async Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        var payload = new JsonObject
        {
            ["requestId"] = request.RequestId.ToString(),
            ["mediaType"] = request.MediaType.ToString().ToLowerInvariant(),
            ["work"] = SerializeWork(request.Work),
            ["edition"] = request.Edition is null ? null : SerializeEdition(request.Edition)
        };

        if (request.Constraints is not null)
        {
            payload["constraints"] = new JsonObject
            {
                ["languages"] = ToJsonArrayOrNull(request.Constraints.Languages),
                ["formats"] = ToJsonArrayOrNull(request.Constraints.Formats),
                ["excludeCollections"] = request.Constraints.ExcludeCollections
            };
        }

        if (request.Pagination is not null)
        {
            payload["pagination"] = new JsonObject
            {
                ["limit"] = request.Pagination.Limit,
                ["cursor"] = request.Pagination.Cursor
            };
        }

        using var response = await client.PostAsync("search", JsonContent.Create(payload), cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var candidatesNode = json?["candidates"]?.AsArray();
        if (candidatesNode is null)
        {
            return [];
        }

        var results = new List<ExternalProviderCandidate>();
        foreach (var node in candidatesNode)
        {
            var reference = node?["providerReference"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(reference) || node is null)
            {
                continue;
            }

            results.Add(new ExternalProviderCandidate(
                reference,
                ParseWork(node),
                ParseEdition(node["edition"]),
                ParseRelease(node["release"]),
                node["candidateRevision"]?.GetValue<string>(),
                node["acquireToken"]?.GetValue<string>(),
                node["extensions"]?.ToJsonString()));
        }

        return results;
    }

    private static JsonObject SerializeWork(ExternalProviderWorkEvidence work) => new()
    {
        ["title"] = work.Title,
        ["subtitle"] = work.Subtitle,
        ["authors"] = new JsonArray(work.Authors
            .Select(author => (JsonNode)new JsonObject { ["name"] = author.Name, ["role"] = author.Role })
            .ToArray()),
        ["series"] = new JsonArray(work.Series
            .Select(series => (JsonNode)new JsonObject { ["name"] = series.Name, ["position"] = series.Position })
            .ToArray()),
        ["identifiers"] = SerializeIdentifiers(work.Identifiers)
    };

    private static JsonObject SerializeEdition(ExternalProviderEditionEvidence edition) => new()
    {
        ["language"] = edition.Language,
        ["publicationYear"] = edition.PublicationYear,
        ["publisher"] = edition.Publisher,
        ["identifiers"] = SerializeIdentifiers(edition.Identifiers)
    };

    private static JsonArray SerializeIdentifiers(IReadOnlyList<BookIdentifier> identifiers) => new(identifiers
        .Select(identifier => (JsonNode)new JsonObject { ["scheme"] = identifier.Scheme, ["value"] = identifier.Value })
        .ToArray());

    private static JsonArray? ToJsonArrayOrNull(IReadOnlyList<string>? values) =>
        values is null ? null : new JsonArray(values.Select(value => (JsonNode)JsonValue.Create(value)).ToArray());

    /// <summary>
    /// Tolerant of both the v2 nested <c>work</c> object and a legacy flat
    /// <c>title</c>/<c>author</c> candidate (protocol v1, Appendix A) — a
    /// provider that has not upgraded still parses cleanly.
    /// </summary>
    private static ExternalProviderWorkEvidence ParseWork(JsonNode node)
    {
        var workNode = node["work"];
        if (workNode is null)
        {
            return new ExternalProviderWorkEvidence(
                node["title"]?.GetValue<string>() ?? string.Empty,
                null,
                node["author"]?.GetValue<string>() is { } legacyAuthor ? [new BookAuthor(legacyAuthor, "author")] : [],
                [],
                []);
        }

        return new ExternalProviderWorkEvidence(
            workNode["title"]?.GetValue<string>() ?? string.Empty,
            workNode["subtitle"]?.GetValue<string>(),
            ParseAuthors(workNode["authors"]),
            ParseSeries(workNode["series"]),
            ParseIdentifiers(workNode["identifiers"]));
    }

    private static ExternalProviderEditionEvidence? ParseEdition(JsonNode? editionNode) => editionNode is null
        ? null
        : new ExternalProviderEditionEvidence(
            editionNode["language"]?.GetValue<string>(),
            editionNode["publicationYear"]?.GetValue<int?>(),
            editionNode["publisher"]?.GetValue<string>(),
            ParseIdentifiers(editionNode["identifiers"]));

    private static ExternalProviderReleaseEvidence? ParseRelease(JsonNode? releaseNode) => releaseNode is null
        ? null
        : new ExternalProviderReleaseEvidence(
            releaseNode["name"]?.GetValue<string>(),
            releaseNode["format"]?.GetValue<string>(),
            releaseNode["sizeBytes"]?.GetValue<long?>(),
            releaseNode["isCollection"]?.GetValue<bool?>(),
            releaseNode["partCount"]?.GetValue<int?>(),
            releaseNode["isSample"]?.GetValue<bool?>(),
            releaseNode["isAbridged"]?.GetValue<bool?>(),
            releaseNode["isUnabridged"]?.GetValue<bool?>(),
            releaseNode["qualityTags"]?.AsArray().Select(tag => tag?.GetValue<string>() ?? string.Empty).ToArray() ?? [],
            releaseNode["ageDays"]?.GetValue<int?>());

    private static List<BookAuthor> ParseAuthors(JsonNode? authorsNode)
    {
        if (authorsNode is not JsonArray array)
        {
            return [];
        }

        var results = new List<BookAuthor>();
        foreach (var node in array)
        {
            // Tolerates a legacy flat array of plain author-name strings
            // alongside the v2 {name, role} object shape.
            if (node is JsonValue value && value.TryGetValue(out string? name))
            {
                results.Add(new BookAuthor(name, "author"));
            }
            else if (node is not null)
            {
                var authorName = node["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(authorName))
                {
                    results.Add(new BookAuthor(authorName, node["role"]?.GetValue<string>()));
                }
            }
        }

        return results;
    }

    private static List<BookSeries> ParseSeries(JsonNode? seriesNode)
    {
        if (seriesNode is not JsonArray array)
        {
            return [];
        }

        var results = new List<BookSeries>();
        foreach (var node in array)
        {
            var name = node?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                results.Add(new BookSeries(name, node!["position"]?.GetValue<string>()));
            }
        }

        return results;
    }

    private static List<BookIdentifier> ParseIdentifiers(JsonNode? identifiersNode)
    {
        if (identifiersNode is JsonObject legacyObject)
        {
            // Tolerates v1's fixed {isbn13: "..."} shape.
            var results = new List<BookIdentifier>();
            foreach (var (scheme, valueNode) in legacyObject)
            {
                var value = valueNode?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    results.Add(new BookIdentifier(scheme, value));
                }
            }

            return results;
        }

        if (identifiersNode is not JsonArray array)
        {
            return [];
        }

        var identifiers = new List<BookIdentifier>();
        foreach (var node in array)
        {
            var scheme = node?["scheme"]?.GetValue<string>();
            var value = node?["value"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(scheme) && !string.IsNullOrWhiteSpace(value))
            {
                identifiers.Add(new BookIdentifier(scheme, value));
            }
        }

        return identifiers;
    }

    public async Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken)
    {
        var client = CreateClient(baseUrl, apiKey, route);
        try
        {
            var payload = new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString(),
                ["candidateReference"] = candidateReference,
                ["mediaType"] = mediaType.ToString().ToLowerInvariant()
            };

            using var acquireResponse = await client.PostAsync("acquire", JsonContent.Create(payload), cancellationToken);
            acquireResponse.EnsureSuccessStatusCode();
            var acquireJson = JsonNode.Parse(await acquireResponse.Content.ReadAsStringAsync(cancellationToken))
                ?? throw new HttpRequestException("The acquire response was not valid JSON.");
            var jobId = acquireJson["jobId"]?.GetValue<string>()
                ?? throw new HttpRequestException("The acquire response did not include a jobId.");
            var jobPath = $"acquire/{Uri.EscapeDataString(jobId)}";

            await PollUntilCompletedAsync(client, jobPath, cancellationToken);

            var artifactResponse = await client.GetAsync(
                $"{jobPath}/artifact", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            artifactResponse.EnsureSuccessStatusCode();

            var filename = artifactResponse.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                ?? artifactResponse.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"{candidateReference}.bin";
            var stream = await artifactResponse.Content.ReadAsStreamAsync(cancellationToken);

            // The stream is still backed by this response/client's connection —
            // both must outlive the caller's read, so ownership transfers to the
            // wrapper rather than being disposed here.
            return new ExternalProviderArtifact(new HttpResponseOwnedStream(stream, artifactResponse, client), filename);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        var payload = new JsonObject
        {
            ["requestId"] = request.RequestId.ToString(),
            ["candidateReference"] = request.CandidateReference,
            ["candidateRevision"] = request.CandidateRevision,
            ["acquireToken"] = request.AcquireToken,
            ["mediaType"] = request.MediaType.ToString().ToLowerInvariant()
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "acquire")
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflictJson = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var code = conflictJson?["code"]?.GetValue<string>();
            if (string.Equals(code, "CANDIDATE_CHANGED", StringComparison.OrdinalIgnoreCase))
            {
                return ExternalProviderAcquireSubmission.CandidateChanged;
            }
        }

        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new HttpRequestException("The acquire response was not valid JSON.");
        var jobId = json["jobId"]?.GetValue<string>()
            ?? throw new HttpRequestException("The acquire response did not include a jobId.");

        return ExternalProviderAcquireSubmission.Accepted(
            jobId,
            ParseLifecycleState(json["state"]?.GetValue<string>()),
            json["phase"]?.GetValue<string>(),
            ParsePollAfterSeconds(response, json));
    }

    public async Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        using var response = await client.GetAsync($"acquire/{Uri.EscapeDataString(jobId)}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new HttpRequestException("The job status response was not valid JSON.");

        var state = ParseLifecycleState(json["state"]?.GetValue<string>());
        var interactionNode = json["interaction"];
        var interaction = interactionNode is null
            ? null
            : new ProviderInteraction(
                interactionNode["type"]?.GetValue<string>(),
                interactionNode["message"]?.GetValue<string>(),
                interactionNode["expiresAt"]?.GetValue<DateTimeOffset?>(),
                interactionNode["resumeSupported"]?.GetValue<bool?>(),
                interactionNode["actionUrl"]?.GetValue<string>());

        var progressNode = json["progress"];
        var progress = progressNode is null
            ? null
            : new ProviderProgress(
                progressNode["percent"]?.GetValue<double?>(),
                progressNode["bytesCompleted"]?.GetValue<long?>(),
                progressNode["bytesTotal"]?.GetValue<long?>(),
                progressNode["message"]?.GetValue<string>());

        var errorNode = json["error"];
        var error = errorNode is null
            ? null
            : new ProviderJobError(
                errorNode["code"]?.GetValue<string>(),
                errorNode["message"]?.GetValue<string>(),
                errorNode["retryable"]?.GetValue<bool?>(),
                errorNode["retryAfterSeconds"]?.GetValue<int?>(),
                errorNode["details"]?.ToJsonString());

        return new ExternalProviderJobStatus(
            jobId, state, json["phase"]?.GetValue<string>(), interaction, progress, error,
            ParsePollAfterSeconds(response, json));
    }

    public async Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        using var response = await client.GetAsync($"acquire/{Uri.EscapeDataString(jobId)}/outputs", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var outputsNode = json?["outputs"]?.AsArray();
        if (outputsNode is null)
        {
            return [];
        }

        var results = new List<ExternalProviderOutput>();
        foreach (var node in outputsNode)
        {
            var outputId = node?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(outputId))
            {
                continue;
            }

            results.Add(new ExternalProviderOutput(
                outputId,
                ParseOutputKind(node!["kind"]?.GetValue<string>()),
                node["role"]?.GetValue<string>(),
                node["filename"]?.GetValue<string>(),
                node["contentType"]?.GetValue<string>(),
                node["sizeBytes"]?.GetValue<long?>(),
                node["uri"]?.GetValue<string>(),
                node["uriScheme"]?.GetValue<string>(),
                node["checksums"]?.ToJsonString(),
                node["retention"]?["expiresAt"]?.GetValue<DateTimeOffset?>()));
        }

        return results;
    }

    public async Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
        CancellationToken cancellationToken)
    {
        var client = CreateClient(baseUrl, apiKey, route);
        try
        {
            var response = await client.GetAsync(
                $"acquire/{Uri.EscapeDataString(jobId)}/outputs/{Uri.EscapeDataString(outputId)}",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var filename = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"{outputId}.bin";
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            return new ExternalProviderArtifact(new HttpResponseOwnedStream(stream, response, client), filename);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        try
        {
            using var response = await client.PostAsync(
                $"acquire/{Uri.EscapeDataString(jobId)}/cancel", content: null, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Best-effort per §8b — the job may still complete or fail on its own.
        }
    }

    public async Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        try
        {
            using var response = await client.DeleteAsync($"acquire/{Uri.EscapeDataString(jobId)}", cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Best-effort per §8b.
        }
    }

    private static ProviderAcquisitionJobLifecycleState ParseLifecycleState(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "queued" => ProviderAcquisitionJobLifecycleState.Queued,
            "running" => ProviderAcquisitionJobLifecycleState.Running,
            "waiting" => ProviderAcquisitionJobLifecycleState.Waiting,
            "completed" => ProviderAcquisitionJobLifecycleState.Completed,
            "failed" => ProviderAcquisitionJobLifecycleState.Failed,
            "cancelled" => ProviderAcquisitionJobLifecycleState.Cancelled,
            // A v1 provider's InProgress/Completed/Failed vocabulary — tolerated
            // rather than rejected while both protocol versions are in play.
            "inprogress" => ProviderAcquisitionJobLifecycleState.Running,
            _ => ProviderAcquisitionJobLifecycleState.Running
        };

    private static ProviderOutputKind ParseOutputKind(string? value) => value?.ToLowerInvariant() switch
    {
        "uri" => ProviderOutputKind.Uri,
        "descriptor" => ProviderOutputKind.Descriptor,
        _ => ProviderOutputKind.File
    };

    /// <summary>Prefers the standard <c>Retry-After</c> header; falls back to the body-level <c>pollAfterSeconds</c> hint (protocol v2 §8).</summary>
    private static int? ParsePollAfterSeconds(HttpResponseMessage response, JsonNode json) =>
        (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds ?? json["pollAfterSeconds"]?.GetValue<int?>();

    private static async Task PollUntilCompletedAsync(HttpClient client, string jobPath, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AcquireTimeout);

        try
        {
            while (true)
            {
                using var statusResponse = await client.GetAsync(jobPath, timeoutCts.Token);
                statusResponse.EnsureSuccessStatusCode();
                var statusJson = JsonNode.Parse(await statusResponse.Content.ReadAsStringAsync(timeoutCts.Token));
                var status = statusJson?["status"]?.GetValue<string>() ?? "InProgress";

                if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException(
                        statusJson?["failureReason"]?.GetValue<string>() ?? "The provider reported the job failed.");
                }

                await Task.Delay(AcquirePollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TryCancelAsync(client, jobPath);
            throw new TimeoutException("The provider did not complete the acquisition in time.");
        }
        catch (OperationCanceledException)
        {
            await TryCancelAsync(client, jobPath);
            throw;
        }
    }

    private static async Task TryCancelAsync(HttpClient client, string jobPath)
    {
        try
        {
            using var response = await client.DeleteAsync(jobPath, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            // Best-effort only — the job may finish anyway; staging never sees a partial file either way.
        }
    }

    private HttpClient CreateClient(string baseUrl, string? apiKey, EgressRoute route)
    {
        var client = route is EgressRoute.GatewayRoute gatewayRoute
            ? new HttpClient(
                new SocketsHttpHandler { Proxy = new WebProxy(gatewayRoute.ProxyEndpoint), UseProxy = true },
                disposeHandler: true)
            : httpClientFactory.CreateClient();

        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(20);
        client.MaxResponseContentBufferSize = MaxJsonResponseBytes;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return client;
    }

    /// <summary>
    /// A response stream whose disposal also disposes the <see cref="HttpResponseMessage"/>
    /// and <see cref="HttpClient"/> that back it — both must stay alive for as
    /// long as the caller is reading, since a fresh, non-pooled client is built
    /// per external-provider call.
    /// </summary>
    private sealed class HttpResponseOwnedStream(Stream inner, HttpResponseMessage response, HttpClient client) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            client.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
