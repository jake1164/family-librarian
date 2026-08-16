using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FamilyLibrarian.SampleProvider;

/// <summary>
/// Builds the sample provider's endpoints. Separated from <c>Program.cs</c> so
/// the conformance test (<c>ExternalProviderClientTests</c>) can build and start
/// a real instance directly, without going through <c>app.Run()</c>'s blocking
/// loop or <c>WebApplicationFactory</c>'s <c>TestServer</c>-only assumptions.
/// </summary>
public static class SampleProviderHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        var apiKey = Environment.GetEnvironmentVariable("SAMPLE_PROVIDER_API_KEY");
        var catalog = new[]
        {
            new SampleCandidate("pride-and-prejudice", "Pride and Prejudice", "Jane Austen", "epub"),
            new SampleCandidate("frankenstein", "Frankenstein", "Mary Wollstonecraft Shelley", "epub")
        };
        var jobs = new ConcurrentDictionary<string, SampleJob>();
        var manifestCapabilities = new[] { "ebook", "search", "acquire" };

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

        app.MapGet("/manifest", () => Results.Ok(new
        {
            protocolVersion = "1",
            id = "sample-provider",
            name = "Family Librarian Sample Provider",
            version = "1.0.0",
            capabilities = manifestCapabilities,
            egressPolicy = "NORMAL"
        }));

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
                    title = candidate.Title,
                    author = candidate.Author,
                    format = candidate.Format,
                    sizeBytes = (long?)null
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

            var jobId = Guid.NewGuid().ToString("N");
            // Ready after a short, genuine delay — not synchronous — so a real
            // client exercises real polling, not a stub that completes on the
            // first check.
            jobs[jobId] = new SampleJob(candidate, DateTimeOffset.UtcNow.AddSeconds(3));

            return Results.Json(new { jobId, status = "InProgress" }, statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet("/acquire/{jobId}", (string jobId) =>
        {
            if (!jobs.TryGetValue(jobId, out var job))
            {
                return Results.NotFound();
            }

            var status = DateTimeOffset.UtcNow >= job.ReadyAtUtc ? "Completed" : "InProgress";
            return Results.Ok(new { jobId, status });
        });

        app.MapGet("/acquire/{jobId}/artifact", (string jobId) =>
        {
            if (!jobs.TryGetValue(jobId, out var job) || DateTimeOffset.UtcNow < job.ReadyAtUtc)
            {
                return Results.NotFound();
            }

            var bytes = SampleEpub.Build(job.Candidate.Title, job.Candidate.Author);
            return Results.File(bytes, "application/epub+zip", $"{job.Candidate.Reference}.epub");
        });

        app.MapDelete("/acquire/{jobId}", (string jobId) =>
        {
            jobs.TryRemove(jobId, out _);
            return Results.NoContent();
        });

        return app;
    }
}

internal sealed record SampleCandidate(string Reference, string Title, string Author, string Format);

internal sealed record SampleJob(SampleCandidate Candidate, DateTimeOffset ReadyAtUtc);

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
