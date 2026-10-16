using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security;
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
    private static readonly string[] MediaTypesWithAudiobook = ["ebook", "audiobook"];
    private static readonly string[] Operations = ["search", "acquire"];
    private static readonly string[] Features = ["checksums", "waiting-interaction"];

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // One simulated job "stage" (see SampleJob.State). Defaults to a real
        // second so a deployed sample provider, such as the lab's, exercises
        // genuine polling cadence; the in-repo conformance tests shorten it
        // so they prove the same state sequence without sleeping for it.
        var jobStage = TimeSpan.FromMilliseconds(
            builder.Configuration.GetValue("SampleProvider:JobStageMilliseconds", 1000));
        var app = builder.Build();

        var apiKey = Environment.GetEnvironmentVariable("SAMPLE_PROVIDER_API_KEY");
        // Off by default, so the conformance tests and every existing lab case see exactly the catalog below.
        // The family-librarian-lab sets it to add the Phase 5 requester-question candidates (QuestionFixtures),
        // an audiobook media type, and the /test/* control endpoints.
        var fixtures = Environment.GetEnvironmentVariable("SAMPLE_PROVIDER_QUESTION_FIXTURES") == "1";
        var baseCatalog = new[]
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
        var catalog = fixtures ? baseCatalog.Concat(QuestionFixtures()).ToArray() : baseCatalog;
        // Fixture candidates that are switched off until a lab case enables them (e.g. a standalone copy that
        // "appears later"). Only meaningful with SAMPLE_PROVIDER_QUESTION_FIXTURES=1.
        var enabledFixtures = new ConcurrentDictionary<string, bool>(
            catalog.Where(candidate => candidate.Fixture).ToDictionary(candidate => candidate.Reference, candidate => candidate.EnabledInitially));
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
                mediaTypes = fixtures ? MediaTypesWithAudiobook : MediaTypes,
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
            var requestedMedia = body?["mediaType"]?.GetValue<string>() ?? string.Empty;

            var matches = catalog
                .Where(candidate => candidate.Fixture
                    // A fixture is returned only for the titles it lists, for the media type it is, and only while
                    // enabled: the substring rule below would never return a collection for a single book.
                    ? enabledFixtures.GetValueOrDefault(candidate.Reference) &&
                      candidate.Aliases.Any(alias => string.Equals(alias, title, StringComparison.OrdinalIgnoreCase)) &&
                      (requestedMedia.Length == 0 ||
                       string.Equals(candidate.Format == "epub" ? "ebook" : "audiobook", requestedMedia, StringComparison.OrdinalIgnoreCase))
                    : title.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase) ||
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
                        language = candidate.Language,
                        publicationYear = candidate.PublicationYear,
                        publisher = candidate.Publisher,
                        identifiers = Array.Empty<object>()
                    },
                    release = new
                    {
                        name = candidate.ReleaseName ?? $"{candidate.Reference}.{candidate.Format}",
                        format = candidate.Format,
                        sizeBytes = (long?)null,
                        isCollection = candidate.IsCollection,
                        isAbridged = candidate.IsAbridged ? (bool?)true : null,
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

            if (candidate.Fixture && !enabledFixtures.GetValueOrDefault(candidate.Reference))
            {
                return Results.NotFound(new { message = "Unknown candidateReference." });
            }

            if (candidate.Format != "epub")
            {
                // The audiobook fixtures exist only to be searched (to reach a requester question); this
                // provider has no audio to serve.
                return Results.Json(
                    new { code = "NOT_ACQUIRABLE", message = "This fixture candidate cannot be fetched.", retryable = false },
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    contentType: "application/problem+json");
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
            var job = new SampleJob(candidate, DateTimeOffset.UtcNow, jobStage);
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

        if (fixtures)
        {
            // Lab control plane for the Phase 5 cases: not part of the provider protocol.
            app.MapPost("/test/catalog/{reference}/{action}", (string reference, string action) =>
            {
                if (!enabledFixtures.ContainsKey(reference) || action is not ("enable" or "disable"))
                {
                    return Results.NotFound();
                }

                enabledFixtures[reference] = action == "enable";
                return Results.NoContent();
            });
            app.MapPost("/test/reset", () =>
            {
                foreach (var candidate in catalog.Where(candidate => candidate.Fixture))
                {
                    enabledFixtures[candidate.Reference] = candidate.EnabledInitially;
                }

                return Results.NoContent();
            });
            app.MapGet("/test/state", () => Results.Ok(new
            {
                fixtures = enabledFixtures.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value)
            }));
        }

        return app;
    }

    /// <summary>
    /// Phase 5 requester-question fixtures (family-librarian-lab, <c>tests/test_acquisition_questions.py</c>).
    /// Every title, author and series here is invented, so no real book is a test fixture. Each candidate lists
    /// the requested titles it answers (<c>Aliases</c>) because the collections' own titles do not contain the
    /// single book they hold. The matching demo-catalog works are in <c>DemoBookMetadataProvider</c>.
    /// </summary>
    private static SampleCandidate[] QuestionFixtures() =>
    [
        // Collection that holds "Cinder Gate" (#2): the range "Books 1-3" contains it, and its table of contents lists it.
        new("lantern-books-1-3", "The Lantern Cycle Books 1-3", "Mara Quillon", "epub", RequiresInteraction: false,
            Publisher: "Fixture Press", IsCollection: true, Fixture: true, Aliases: ["Cinder Gate"],
            Contents: ["Ember Road", "Cinder Gate", "Ashen Crown"]),
        // The same-titled collection whose table of contents does NOT list "Ashen Crown" (#3): taking it must be held.
        new("lantern-books-1-3-short", "The Lantern Cycle Books 1-3", "Mara Quillon", "epub", RequiresInteraction: false,
            Publisher: "Fixture Press", IsCollection: true, Fixture: true, Aliases: ["Ashen Crown"],
            Contents: ["Ember Road", "Cinder Gate"]),
        // A standalone copy of "Cinder Gate" that is off until a case enables it ("appears later").
        new("lantern-cinder-standalone", "Cinder Gate", "Mara Quillon", "epub", RequiresInteraction: false,
            Publisher: "Fixture Press", SeriesName: "The Lantern Cycle", SeriesPosition: "2",
            Fixture: true, EnabledInitially: false, Aliases: ["Cinder Gate"]),
        // A collection whose contents cannot be told from its title: no question, whatever else happens.
        new("lantern-box-set", "The Lantern Cycle Box Set", "Mara Quillon", "epub", RequiresInteraction: false,
            Publisher: "Fixture Press", IsCollection: true, Fixture: true, Aliases: ["Ember Road"]),
        // Abridged-only audiobook.
        new("windfall-abridged", "Windfall Hollow", "Tessa Brandt", "m4b", RequiresInteraction: false,
            Publisher: "Fixture Audio", IsAbridged: true, Fixture: true, Aliases: ["Windfall Hollow"]),
        // Only a copy in another language.
        new("hollowmere-fr", "Hollowmere", "Odile Marchetti", "epub", RequiresInteraction: false,
            Publisher: "Fixture Press", Language: "fr", Fixture: true, Aliases: ["Hollowmere"]),
        // Only part of an audiobook.
        new("saltmarsh-part-1", "Saltmarsh Light", "Ivo Sandoval", "m4b", RequiresInteraction: false,
            Publisher: "Fixture Audio", ReleaseName: "Saltmarsh Light (1 of 2)", Fixture: true, Aliases: ["Saltmarsh Light"])
    ];
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
    bool IsCollection = false,
    bool Fixture = false,
    bool EnabledInitially = true,
    string[]? Aliases = null,
    string? ReleaseName = null,
    bool IsAbridged = false,
    string Language = "en",
    string[]? Contents = null)
{
    /// <summary>Titles a fixture candidate is returned for; empty for the original catalog.</summary>
    public string[] Aliases { get; init; } = Aliases ?? [];
}

/// <summary>
/// In-memory job state. Not thread-contended in any meaningful way for a
/// sample/conformance-test process — <see cref="Cancelled"/> is the only
/// field mutated after construction.
/// </summary>
internal sealed class SampleJob(SampleCandidate candidate, DateTimeOffset createdAtUtc, TimeSpan stage)
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
                _ when elapsed < stage * 2 => "waiting",
                _ when elapsed < stage * 4 => "running",
                _ => "completed"
            };
        }

        // Ready after a short, genuine delay -- not synchronous -- so a real
        // client exercises real polling, not a stub that completes on the
        // first check.
        return elapsed < stage * 3 ? "running" : "completed";
    }

    public string? Phase(DateTimeOffset now) => State(now) switch
    {
        "waiting" => "user-interaction",
        "running" => "downloading",
        _ => null
    };

    public IReadOnlyList<SampleOutput> BuildOutputs()
    {
        var epubBytes = SampleEpub.Build(Candidate.Title, Candidate.Author, Candidate.Contents);
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
    /// <summary>
    /// A minimal but structurally valid EPUB: a readable ZIP with the stored
    /// "mimetype" entry first, a container pointing at an OPF package, and
    /// package metadata carrying the title and author. Family Librarian's own
    /// EpubValidator and identity verifier both read exactly these, so a
    /// fixture missing any of them is rejected for a reason unrelated to what
    /// the lab case under test is exercising.
    /// </summary>
    public static byte[] Build(string title, string author, IReadOnlyList<string>? contents = null)
    {
        // A collection lists each contained work as its own chapter and navigation entry, so FL's table-of-contents
        // check (and its completeness check, which needs every target to exist) sees a real structure.
        contents ??= [];
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // OCF requires "mimetype" to be the first entry, stored uncompressed.
            WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            WriteEntry(
                archive, "META-INF/container.xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            WriteEntry(
                archive, "OEBPS/content.opf",
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="book-id">urn:uuid:family-librarian-sample-provider</dc:identifier>
                    <dc:title>{SecurityElement.Escape(title)}</dc:title>
                    <dc:creator>{SecurityElement.Escape(author)}</dc:creator>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>
                    <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
                    {(contents.Count == 0 ? string.Empty : "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>")}
                    {string.Concat(contents.Select((_, index) => $"<item id=\"part{index + 1}\" href=\"part{index + 1}.xhtml\" media-type=\"application/xhtml+xml\"/>"))}
                  </manifest>
                  <spine>
                    <itemref idref="chapter"/>
                    {string.Concat(contents.Select((_, index) => $"<itemref idref=\"part{index + 1}\"/>"))}
                  </spine>
                </package>
                """);
            WriteEntry(
                archive, "OEBPS/chapter.xhtml",
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>{SecurityElement.Escape(title)}</title></head>
                <body><p>Fetched from the Family Librarian sample provider.</p></body></html>
                """);
            if (contents.Count > 0)
            {
                WriteEntry(
                    archive, "OEBPS/nav.xhtml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head>
                    <body><nav epub:type="toc"><ol>
                    {string.Concat(contents.Select((name, index) => $"<li><a href=\"part{index + 1}.xhtml\">{SecurityElement.Escape(name)}</a></li>"))}
                    </ol></nav></body></html>
                    """);
                for (var index = 0; index < contents.Count; index++)
                {
                    WriteEntry(
                        archive, $"OEBPS/part{index + 1}.xhtml",
                        $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <html xmlns="http://www.w3.org/1999/xhtml"><head><title>{SecurityElement.Escape(contents[index])}</title></head>
                        <body><h1>{SecurityElement.Escape(contents[index])}</h1><p>Fixture chapter text.</p></body></html>
                        """);
                }
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(name, level);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
