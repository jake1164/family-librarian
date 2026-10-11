using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelTesting;

internal sealed class FakeSettingsStore : IModelTestSettingsStore
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    public ModelTestSettings? Settings { get; set; }

    public int Saves { get; private set; }

    public Task<ModelTestSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

    public Task<ModelTestSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Settings ??= new ModelTestSettings(Now));

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.CompletedTask;
    }

    /// <summary>Settings as an administrator would leave them after configuring a local model.</summary>
    public static ModelTestSettings Configured(bool enabled = true, bool allowCloud = false, string? cloud = null, bool withKey = true)
    {
        var settings = new ModelTestSettings(Now);
        settings.Configure("http://litellm.example.test:4000", "local/primary", "local/comparison", cloud, allowCloud, 30, 384, null, Now);
        if (withKey) settings.SetApiKey("p:secret-key-value", 1, null, Now);
        if (enabled) settings.SetEnabled(true, null, Now);
        return settings;
    }
}

internal sealed class PassThroughProtector : ICredentialProtector
{
    public int FormatVersion => 1;

    public string Protect(string providerId, string plaintext) => $"p:{plaintext}";

    public string? Unprotect(string providerId, string protectedValue, int formatVersion) =>
        protectedValue.StartsWith("p:", StringComparison.Ordinal) ? protectedValue[2..] : null;
}

internal sealed class CapturingAudit : IAuditWriter
{
    public List<(string Action, string Subject, string Json)> Events { get; } = [];

    public Task WriteAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken)
    {
        Events.Add((action, subjectType, JsonSerializer.Serialize(detail)));
        return Task.CompletedTask;
    }
}

internal sealed class FakeUser(Guid? id) : ICurrentUser
{
    public Guid? UserId { get; } = id;

    public string? DisplayName => UserId is null ? null : "Admin";
}

internal sealed class FakeAdmin(bool isAdministrator) : ICurrentAdministrator
{
    public bool IsAdministrator { get; } = isAdministrator;
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A scripted model: answers each call from a handler and records everything it was asked.</summary>
internal sealed class ScriptedModel(Func<ModelConnection, ModelRequest, ModelResponse> handler) : IModelClient
{
    public List<(ModelConnection Connection, ModelRequest Request)> Calls { get; } = [];

    public Func<Task>? BeforeAnswer { get; set; }

    public async Task<ModelResponse> CompleteAsync(ModelConnection connection, ModelRequest request, CancellationToken cancellationToken)
    {
        Calls.Add((connection, request));
        if (BeforeAnswer is not null) await BeforeAnswer();
        return handler(connection, request);
    }

    public static ModelResponse Ok(string? content, ModelUsage? usage = null, string? model = "reported-model", IReadOnlyList<ModelToolCall>? tools = null) =>
        new(true, ModelFailureKind.None, null, content, tools ?? [], "stop", model, usage ?? new ModelUsage(10, 5, 15), 12);

    public static ModelResponse Fail(ModelFailureKind kind, string detail = "failed") => ModelResponse.Failed(kind, detail, 7);

    /// <summary>A well-behaved model for every check, answering from the supplied prompt.</summary>
    public static ModelResponse WellBehaved(ModelConnection connection, ModelRequest request)
    {
        if (connection.Model == "fl-capability-test-no-such-model")
        {
            return Fail(ModelFailureKind.ModelUnavailable, "HTTP 400: Invalid model name");
        }

        var user = request.Messages[^1].Content;
        var system = request.Messages[0].Content;
        if (request.Tools is { Count: > 0 })
        {
            return Ok(null, tools: [new ModelToolCall("lookup_catalog_title")]);
        }

        if (system.Contains("connectivity probe", StringComparison.Ordinal)) return Ok("pong");
        if (system.Contains("label messages", StringComparison.Ordinal))
        {
            var intent = user.Contains("weather", StringComparison.OrdinalIgnoreCase) && !user.Contains("by", StringComparison.OrdinalIgnoreCase) ? "OutOfDomain"
                : user.Contains("request", StringComparison.OrdinalIgnoreCase) && user.Contains("ready", StringComparison.OrdinalIgnoreCase) ? "CheckRequestStatus"
                : user.StartsWith("Please get", StringComparison.Ordinal) ? "RequestBook"
                : "FindBook";
            return Ok($"{{\"intent\":\"{intent}\"}}");
        }

        if (system.Contains("Extract facts", StringComparison.Ordinal)) return Ok("{\"title\":\"The Lantern Road\",\"author\":\"Mara Quill\",\"year\":2019}");
        var assessment = user.Contains("Orrin Vale", StringComparison.Ordinal) ? "likely_different" : "likely_match";
        return Ok($"{{\"assessment\":\"{assessment}\",\"reasons\":[{{\"text\":\"The facts agree or disagree.\",\"evidence_id\":\"ev-1\"}}]}}");
    }
}
