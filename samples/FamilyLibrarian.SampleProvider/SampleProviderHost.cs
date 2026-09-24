using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FamilyLibrarian.SampleProvider;

/// <summary>
/// Builds the sample provider's endpoints. Separated from <c>Program.cs</c> so
/// the conformance test (<c>ExternalProviderClientTests</c>) can build and start
/// a real instance directly, without going through <c>app.Run()</c>'s blocking
/// loop or <c>WebApplicationFactory</c>'s <c>TestServer</c>-only assumptions.
/// </summary>
public static class SampleProviderHost
{
    private static readonly string[] SupportedProtocolVersions = ["1", "2"];
    private static readonly string[] MediaTypes = ["ebook"];
    private static readonly string[] Operations = ["search", "acquire"];
    private static readonly string[] Features = ["checksums", "waiting-interaction"];

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        var apiKey = Environment.GetEnvironmentVariable("SAMPLE_PROVIDER_API_KEY");
        var catalog = new[]
        {
            new SampleCandidate(
                "pride-and-prejudice", "Pride and Prejudice", "Jane Austen", "epub", RequiresInteraction: false,
                Publisher: "T. Egerton", PublicationYear: 1813),
            new SampleCandidate(
                "frankenstein", "Frankenstein", "Mary Wollstonecraft Shelley", "epub", RequiresInteraction: false,
                Publisher: "Lackington, Hughes, Harding, Mavor & Jones", PublicationYear: 1818),
            // Exercises protocol v2's waiting/user-interaction state end to
            // end: a real client sees state=waiting, phase=user-interaction
            // for a few seconds before the job resumes on its own
            // (resumeSupported=true) and completes — standing in for a
            // a browser-gated acquisition source.
            new SampleCandidate(
                "the-time-machine", "The Time Machine", "H. G. Wells", "epub", RequiresInteraction: true,
                PublicationYear: 1895),
            // Exercises protocol v2 §8/§9's CANDIDATE_CHANGED staleness
            // conflict end to end: always declares candidateRevision "rev-1"
            // from /search, but /acquire always rejects it with 409 --
            // standing in for an upstream record that changed between
            // search and acquire.
            new SampleCandidate(
                "debt-of-honor", "Debt of Honor", "Tom Clancy", "epub", RequiresInteraction: false,
                Publisher: "Putnam", PublicationYear: 1994, SeriesName: "Jack Ryan", SeriesPosition: "6",
                CurrentRevision: "rev-1", AlwaysStaleOnAcquire: true),
            // Exercises protocol v2 §7/§10/§16's release-policy rejection
            // end to end: a real client must see isCollection=true and FL's
            // own ExternalReleasePolicy must flag it, never auto-acquire it,
            // regardless of how well title/author/ISBN otherwise match.
            new SampleCandidate(
                "jack-ryan-omnibus", "Jack Ryan Omnibus", "Tom Clancy", "epub", RequiresInteraction: false,
                Publisher: "Putnam", IsCollection: true)
        };
        var jobs = new ConcurrentDictionary<string, SampleJob>();
        var idempotencyKeys = new ConcurrentDictionary<string, string>();
        // Generated once per process start, held for the process's lifetime —
        // stands in for "persist to disk/env across restarts" (protocol v2
        // §4), which a short-lived test process has no meaningful analogue for.
        var instanceId = Guid.NewGuid().ToString("N");

        app.Use(async (context, next) =>
        {
            if (!string.IsNullOrEmpty(apiKey))
            {
                var provided = context.Request.Headers.Authorization.ToString();
                if (!string.Equals(provided, $"Bearer {apiKey}", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            await next();
        });

        // Speaks both protocol versions so the same process can back both the
        // legacy conformance tests and new protocol-v2 negotiation tests —
        // see docs/04-external-provider-http-protocol.md.
        app.MapGet("/manifest", () => Results.Ok(new
        {
            protocolVersions = SupportedProtocolVersions,
            protocolVersion = "2",
            instanceId,
            id = "sample-provider",
            name = "Family Librarian Sample Provider",
            version = "1.0.0",
            capabilities = new
            {
                mediaTypes = MediaTypes,
                operations = Operations,
                features = Features
            }
        }));

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            operations = new { search = "available", acquire = "available" }
        }));

        app.MapPost("/search", async (HttpRequest request) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            var title = body?["work"]?["title"]?.GetValue<string>() ?? string.Empty;

            var matches = catalog
                .Where(candidate =>
                    title.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase) ||
                    candidate.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => new
                {
                    providerReference = candidate.Reference,
                    candidateRevision = candidate.CurrentRevision,
                    acquireToken = (string?)null,
                    work = new
                    {
                        title = candidate.Title,
                        subtitle = (string?)null,
                        authors = new[] { new { name = candidate.Author, role = "author" } },
                        series = candidate.SeriesName is null
                            ? Array.Empty<object>()
                            : [new { name = candidate.SeriesName, position = candidate.SeriesPosition }],
                        identifiers = Array.Empty<object>()
                    },
                    edition = new
                    {
                        language = "en",
                        publicationYear = candidate.PublicationYear,
                        publisher = candidate.Publisher,
                        identifiers = Array.Empty<object>()
                    },
                    release = new
                    {
                        name = $"{candidate.Reference}.{candidate.Format}",
                        format = candidate.Format,
                        sizeBytes = (long?)null,
                        isCollection = candidate.IsCollection,
                        isSample = false,
                        drm = "none"
                    },
                    extensions = new { }
                });

            return Results.Ok(new { candidates = matches });
        });

        app.MapPost("/acquire", async (HttpRequest request) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            var reference = body?["candidateReference"]?.GetValue<string>();
            var candidate = catalog.FirstOrDefault(candidate => candidate.Reference == reference);
            if (candidate is null)
            {
                return Results.NotFound(new { message = "Unknown candidateReference." });
            }

            if (candidate.AlwaysStaleOnAcquire)
            {
                return Results.Json(
                    new
                    {
                        code = "CANDIDATE_CHANGED",
                        message = "The candidate has changed since it was returned by search.",
                        retryable = false
                    },
                    statusCode: StatusCodes.Status409Conflict,
                    contentType: "application/problem+json");
            }

            // Idempotency-Key replay (protocol v2 §8): a resubmission with a
            // key already seen resolves to the same job, never a duplicate.
            var idempotencyKey = request.Headers["Idempotency-Key"].ToString();
            if (!string.IsNullOrEmpty(idempotencyKey) &&
                idempotencyKeys.TryGetValue(idempotencyKey, out var existingJobId) &&
                jobs.TryGetValue(existingJobId, out var existingJob))
            {
                return Results.Json(
                    new { jobId = existingJobId, status = "InProgress", state = existingJob.State(DateTimeOffset.UtcNow) },
                    statusCode: StatusCodes.Status202Accepted);
            }

            var jobId = Guid.NewGuid().ToString("N");
            var job = new SampleJob(candidate, DateTimeOffset.UtcNow);
            jobs[jobId] = job;
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                idempotencyKeys[idempotencyKey] = jobId;
            }

            return Results.Json(
                new { jobId, status = "InProgress", state = job.State(DateTimeOffset.UtcNow), pollAfterSeconds = 1 },
                statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet("/acquire/{jobId}", (string jobId) =>
        {
            if (!jobs.TryGetValue(jobId, out var job))
            {
                return Results.NotFound();
            }

            var now = DateTimeOffset.UtcNow;
            var state = job.State(now);
            var legacyStatus = state switch
            {
                "completed" => "Completed",
                "failed" => "Failed",
                _ => "InProgress"
            };

            object? interaction = state == "waiting"
                ? new
                {
                    type = "browser",
                    message = "Simulated browser verification — resumes automatically in this sample.",
                    expiresAt = (DateTimeOffset?)null,
                    resumeSupported = true,
                    actionUrl = $"http://sample-provider.invalid/interaction/{jobId}"
                }
                : null;

            return Results.Ok(new
            {
                jobId,
                status = legacyStatus,
                state,
                phase = job.Phase(now),
                interaction,
                pollAfterSeconds = state is "completed" or "failed" or "cancelled" ? (int?)null : 1
            });
        });

        app.MapGet("/acquire/{jobId}/outputs", (string jobId) =>
        {
            if (!jobs.TryGetValue(jobId, out var job) || job.State(DateTimeOffset.UtcNow) != "completed")
            {
                return Results.NotFound();
            }

            return Results.Ok(new { outputs = job.BuildOutputs().Select(output => output.ToWire()) });
        });

        app.MapGet("/acquire/{jobId}/outputs/{outputId}", (string jobId, string outputId) =>
        {
            if (!jobs.TryGetValue(jobId, out var job) || job.State(DateTimeOffset.UtcNow) != "completed")
            {
                return Results.NotFound();
            }

            var output = job.BuildOutputs().FirstOrDefault(candidate => candidate.Id == outputId);
            if (output is null)
            {
                return Results.NotFound();
            }

            return Results.File(output.Bytes, output.ContentType, output.Filename);
        });

        app.MapGet("/acquire/{jobId}/artifact", (string jobId) =>
        {
            if (!jobs.TryGetValue(jobId, out var job) || job.State(DateTimeOffset.UtcNow) != "completed")
            {
                return Results.NotFound();
            }

            var primary = job.BuildOutputs().First(output => output.Id == "primary");
            return Results.File(primary.Bytes, primary.ContentType, primary.Filename);
        });

        app.MapPost("/acquire/{jobId}/cancel", (string jobId) =>
        {
            if (jobs.TryGetValue(jobId, out var job))
            {
                job.Cancelled = true;
            }

            return Results.NoContent();
        });

        app.MapDelete("/acquire/{jobId}", (string jobId) =>
        {
            jobs.TryRemove(jobId, out _);
            return Results.NoContent();
        });

        return app;
    }
}

internal sealed record SampleCandidate(
    string Reference,
    string Title,
    string Author,
    string Format,
    bool RequiresInteraction,
    string? Publisher = null,
    int? PublicationYear = null,
    string? SeriesName = null,
    string? SeriesPosition = null,
    string? CurrentRevision = null,
    bool AlwaysStaleOnAcquire = false,
    bool IsCollection = false);

/// <summary>
/// In-memory job state. Not thread-contended in any meaningful way for a
/// sample/conformance-test process — <see cref="Cancelled"/> is the only
/// field mutated after construction.
/// </summary>
internal sealed class SampleJob(SampleCandidate candidate, DateTimeOffset createdAtUtc)
{
    public SampleCandidate Candidate { get; } = candidate;

    public bool Cancelled { get; set; }

    public string State(DateTimeOffset now)
    {
        if (Cancelled)
        {
            return "cancelled";
        }

        var elapsed = now - createdAtUtc;
        if (Candidate.RequiresInteraction)
        {
            return elapsed switch
            {
                _ when elapsed < TimeSpan.FromSeconds(2) => "waiting",
                _ when elapsed < TimeSpan.FromSeconds(4) => "running",
                _ => "completed"
            };
        }

        // Ready after a short, genuine delay -- not synchronous -- so a real
        // client exercises real polling, not a stub that completes on the
        // first check.
        return elapsed < TimeSpan.FromSeconds(3) ? "running" : "completed";
    }

    public string? Phase(DateTimeOffset now) => State(now) switch
    {
        "waiting" => "user-interaction",
        "running" => "downloading",
        _ => null
    };

    public IReadOnlyList<SampleOutput> BuildOutputs()
    {
        var epubBytes = SampleEpub.Build(Candidate.Title, Candidate.Author);
        var outputs = new List<SampleOutput>
        {
            new("primary", "ebook", $"{Candidate.Reference}.epub", "application/epub+zip", epubBytes)
        };

        // One candidate demonstrates a multi-output job (protocol v2 §8a) --
        // an ebook plus a separate cover -- rather than every job assuming
        // exactly one file.
        if (Candidate.Reference == "pride-and-prejudice")
        {
            var coverBytes = Encoding.UTF8.GetBytes($"Cover placeholder for {Candidate.Title}.\n");
            outputs.Add(new SampleOutput("cover", "cover", "cover.txt", "text/plain", coverBytes));
        }

        return outputs;
    }
}

internal sealed record SampleOutput(string Id, string Role, string Filename, string ContentType, byte[] Bytes)
{
    public object ToWire() => new
    {
        id = Id,
        kind = "file",
        role = Role,
        filename = Filename,
        contentType = ContentType,
        sizeBytes = (long)Bytes.Length,
        checksums = new[] { new { algorithm = "sha256", value = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant() } },
        retention = new { expiresAt = (DateTimeOffset?)null }
    };
}

/// <summary>
/// Builds a minimal, genuinely valid EPUB (a ZIP archive whose first entry is an
/// uncompressed <c>mimetype</c> file) — real enough to pass Family Librarian's
/// content-type/extension sniffing, matching the same minimal-EPUB shape its own
/// test suite uses.
/// </summary>
internal static class SampleEpub
{
    public static byte[] Build(string title, string author)
    {
        using var stream = new MemoryStream();
        WriteStoredEntry(stream, "mimetype", "application/epub+zip");
        WriteStoredEntry(
            stream, "sample.txt",
            $"Fetched from the Family Librarian sample provider.\nTitle: {title}\nAuthor: {author}\n");
        return stream.ToArray();
    }

    private static void WriteStoredEntry(Stream stream, string entryName, string content)
    {
        var nameBytes = Encoding.ASCII.GetBytes(entryName);
        var contentBytes = Encoding.UTF8.GetBytes(content);

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x04034B50u);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write((uint)contentBytes.Length);
        writer.Write((uint)contentBytes.Length);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write(nameBytes);
        writer.Write(contentBytes);
    }
}
