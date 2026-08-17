using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Providers;
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

        return new ExternalProviderManifest(
            json["protocolVersion"]?.GetValue<string>() ?? "1",
            json["id"]?.GetValue<string>() ?? string.Empty,
            json["name"]?.GetValue<string>() ?? string.Empty,
            json["version"]?.GetValue<string>() ?? string.Empty,
            json["capabilities"]?.AsArray().Select(node => node?.GetValue<string>() ?? string.Empty).ToArray() ?? [],
            json["egressPolicy"]?.GetValue<string>() ?? "NORMAL");
    }

    public async Task<bool> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        try
        {
            using var response = await client.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseUrl, apiKey, route);
        var payload = new JsonObject
        {
            ["requestId"] = request.RequestId.ToString(),
            ["mediaType"] = request.MediaType.ToString().ToLowerInvariant(),
            ["work"] = new JsonObject
            {
                ["title"] = request.Title,
                ["authors"] = new JsonArray(request.Authors.Select(author => (JsonNode)JsonValue.Create(author)).ToArray()),
                ["identifiers"] = new JsonObject { ["isbn13"] = request.Isbn13 }
            }
        };

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
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            results.Add(new ExternalProviderCandidate(
                reference,
                node!["title"]?.GetValue<string>() ?? string.Empty,
                node["author"]?.GetValue<string>(),
                node["format"]?.GetValue<string>(),
                node["sizeBytes"]?.GetValue<long?>(),
                node["metadata"]?.ToJsonString()));
        }

        return results;
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
