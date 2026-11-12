using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Tests.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

internal sealed class FakeCaptureWriter : IDecisionCaptureWriter
{
    public List<DecisionCaptureWrite> Writes { get; } = [];

    public bool Throws { get; set; }

    public Task WriteAsync(DecisionCaptureWrite write, CancellationToken cancellationToken)
    {
        if (Throws) throw new InvalidOperationException("capture database down");
        Writes.Add(write);
        return Task.CompletedTask;
    }
}

internal sealed class FakeCaptureLog : IDecisionCaptureLog
{
    public List<string> Entries { get; } = [];

    public void TooLarge(ModelRole role) => Entries.Add($"toolarge:{role}");

    public void Failed(ModelRole? role, string exceptionType) => Entries.Add($"failed:{role}:{exceptionType}");
}

/// <summary>A sink that fails in the worst way a sink can: by throwing from the call the service awaits.</summary>
internal sealed class ThrowingSink : IDecisionCaptureSink
{
    public int Calls { get; private set; }

    public Task CaptureAsync(CapturedDecision decision, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("sink exploded");
    }
}

/// <summary>Real policy, real resolver, real recorder over fakes at the edges: the whole capture path except the database.</summary>
internal sealed class CaptureRig
{
    public static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public CaptureRig(ModelRole role, Func<ModelConnection, ModelRequest, ModelResponse> model, ModelReasoningMode reasoning = ModelReasoningMode.Inherit,
        bool decisionCapture = true, bool chatCapture = false, int maxOutput = 1024, bool enableRole = true,
        bool cloud = false, bool allowCloudExcerpts = false)
    {
        Settings = new ModelGatewaySettings(Now);
        Settings.Configure("http://litellm.example.test:4000", cloud, null, Now);
        Settings.SetAllowCloudExcerpts(allowCloudExcerpts, null, Now);
        Settings.ConfigureRole(role, enableRole, cloud ? ModelPlacement.Cloud : ModelPlacement.Local, "local/alias", "cloud/alias", null, Now, reasoning);
        Settings.ConfigureLimits(ModelExecutionLimits.Default with { MaxOutputTokens = maxOutput, CloudDailyRequests = cloud ? 100 : 0, CloudDailyTokens = cloud ? 1_000_000 : 0 }, null, Now);
        Settings.ConfigureDecisionCapture(decisionCapture, chatCapture, DecisionCaptureLimits.Default, null, Now);
        Settings.SetEnabled(true, null, Now);
        var store = new FakeGatewayStore { Settings = Settings };
        Model = new ScriptedModel(model);
        Ledger = new FakeLedger();
        Clock = new MutableClock(Now);
        Resolver = new ModelRoleResolver(store, new PassThroughProtector());
        Policy = new ModelExecutionPolicy(Resolver, Model, Ledger, new ModelExecutionGate(), new ModelCircuitBreaker(), Clock);
        Writer = new FakeCaptureWriter();
        Log = new FakeCaptureLog();
        Sink = new DecisionCaptureRecorder(store, Writer, Clock, Log);
    }

    public ModelGatewaySettings Settings { get; }

    public ScriptedModel Model { get; }

    public FakeLedger Ledger { get; }

    public MutableClock Clock { get; }

    public ModelRoleResolver Resolver { get; }

    public ModelExecutionPolicy Policy { get; }

    public FakeCaptureWriter Writer { get; }

    public FakeCaptureLog Log { get; }

    public DecisionCaptureRecorder Sink { get; }
}

internal static class CaptureData
{
    public const string SecretToken = "acquire-token-SECRET-123";
    public const string SecretRevision = "revision-SECRET-456";

    public static readonly BookIdentity Requested = new("The Lantern Road", "Mara Quill", ["9781234567897"],
        [new BookAuthor("Mara Quill", "author")], [new BookSeries("Lantern Cycle", "2")], "en", 2019, "Ember Press", ["Lantern Road"]);

    /// <summary>An option as production builds one: the identity assessment comes from the real resolver, and provider-only fields are present.</summary>
    public static FulfillmentOption Option(
        BookIdentity requested, int number, string title, string author, string releaseName, string format = "epub",
        RequestMediaType mediaType = RequestMediaType.Ebook, bool sample = false)
    {
        var source = new ExternalProviderCandidate(
            "provider-ref-" + number,
            new ExternalProviderWorkEvidence(title, null, [new BookAuthor(author, "author")], [], []),
            new ExternalProviderEditionEvidence("en", 2019, "Ember Press", [new BookIdentifier("isbn13", "9781234567897"), new BookIdentifier("asin", "B000TEST")]),
            new ExternalProviderReleaseEvidence(releaseName, format, 1_500_000 + number, false, null, sample, false, null, [], null),
            SecretRevision, SecretToken, null, new Uri("https://example.test/inspect/" + number), "origin summary");
        var assessment = DeterministicCandidateIdentityResolver.Assess(requested, source);
        return new FulfillmentOption(
            "example-source", "result-" + number, Guid.Empty, null, mediaType, OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload,
            format, "en", null, null, null, null, null, null, null, "provider-data-SECRET",
            Title: title, Author: author, CandidateRevision: SecretRevision, AcquireToken: SecretToken, PublicationYear: 2019, Publisher: "Ember Press",
            SizeBytes: 1_500_000 + number, ReleaseName: releaseName, AuthorAffinity: assessment.AuthorEvidence, HasPlausibleTitle: true,
            IdentityAssessment: assessment, AdminInspectionUri: new Uri("https://example.test/inspect/" + number), AdminSourceSummary: "origin summary");
    }

    public static IReadOnlyList<FulfillmentOption> SearchResults(BookIdentity requested) =>
    [
        Option(requested, 1, "The Lantern Road", "Someone Else", "The.Lantern.Road.Someone.Else"),
        Option(requested, 2, "Lantern Road (Sample)", "Mara Quill", "Lantern.Road.Sample", sample: true),
        Option(requested, 3, "A Different Book", "Mara Quill", "A.Different.Book")
    ];
}
