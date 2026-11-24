using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelTesting;

namespace FamilyLibrarian.Application.ModelGateway;

public sealed record ModelCatalogResult(bool Forbidden, IReadOnlyList<string> Models, string? Problem)
{
    public static ModelCatalogResult NotAllowed() => new(true, [], "Administrator access is required.");
}

/// <summary>
/// Lists the models the shared connection's endpoint serves, for the administrator's pickers. Read-only: it changes no setting,
/// stores nothing and never returns the key.
/// </summary>
public sealed record ModelProbeResult(
    bool Forbidden, bool Reached, IReadOnlyList<string> Models, string? Problem, string? EndpointTested, string KeySource);

public sealed class ModelCatalogService(
    IModelTestTargetSource connections,
    IModelCatalogClient client,
    ICurrentUser currentUser,
    ICurrentAdministrator administrator)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    public async Task<ModelCatalogResult> ListAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || !administrator.IsAdministrator)
        {
            return ModelCatalogResult.NotAllowed();
        }

        var connection = await connections.GetConnectionAsync(cancellationToken);
        if (connection.Endpoint is null)
        {
            return new ModelCatalogResult(false, [], connection.Problem ?? "Set the endpoint first.");
        }

        var result = await client.ListModelsAsync(connection.Endpoint, connection.ApiKey, ListTimeout, cancellationToken);
        return result.Succeeded
            ? new ModelCatalogResult(false, result.ModelIds, null)
            : new ModelCatalogResult(false, [], Describe(result));
    }

    /// <summary>
    /// Tries the values the administrator has typed, before anything is saved. Nothing is stored. The saved key is only sent to the saved
    /// endpoint, so an edited URL can never be used to carry the stored secret somewhere new.
    /// </summary>
    public async Task<ModelProbeResult> ProbeAsync(string? draftEndpoint, string? draftApiKey, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || !administrator.IsAdministrator)
        {
            return new ModelProbeResult(true, false, [], "Administrator access is required.", null, "none");
        }

        var saved = await connections.GetConnectionAsync(cancellationToken);
        var typedEndpoint = !string.IsNullOrWhiteSpace(draftEndpoint);
        var typedKey = !string.IsNullOrWhiteSpace(draftApiKey);

        Uri? endpoint = saved.Endpoint;
        if (typedEndpoint)
        {
            if (ModelConfigurationRules.EndpointError(draftEndpoint) is { } endpointError)
            {
                return Fail(endpointError, null, "none");
            }

            endpoint = new Uri(draftEndpoint!.Trim());
        }

        if (endpoint is null)
        {
            return Fail(saved.Problem ?? "Enter the endpoint URL first.", null, "none");
        }

        var sameAsSaved = saved.Endpoint is not null && Normalize(saved.Endpoint) == Normalize(endpoint);
        string? key;
        string keySource;
        if (typedKey)
        {
            if (ModelConfigurationRules.ApiKeyError(draftApiKey) is { } keyError)
            {
                return Fail(keyError, Normalize(endpoint), "none");
            }

            key = draftApiKey!.Trim();
            keySource = "typed";
        }
        else if (sameAsSaved && saved.ApiKey is not null)
        {
            key = saved.ApiKey;
            keySource = "saved";
        }
        else
        {
            key = null;
            keySource = "none";
        }

        var result = await client.ListModelsAsync(endpoint, key, ListTimeout, cancellationToken);
        var tested = Normalize(endpoint);
        return result.Succeeded
            ? new ModelProbeResult(false, true, result.ModelIds, null, tested, keySource)
            : new ModelProbeResult(false, false, [], DescribeProbe(result, keySource, typedEndpoint && !sameAsSaved), tested, keySource);

        static ModelProbeResult Fail(string problem, string? tested, string source) => new(false, false, [], problem, tested, source);
    }

    /// <summary>The client appends <c>/v1</c> itself, so <c>http://host:4000</c> and <c>http://host:4000/v1</c> are one endpoint.</summary>
    private static string Normalize(Uri endpoint)
    {
        var text = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? text[..^3] : text;
    }

    private static string DescribeProbe(ModelListResult result, string keySource, bool endpointIsNew) => result.Failure switch
    {
        ModelFailureKind.Unauthorized => keySource switch
        {
            "none" when endpointIsNew => "The endpoint refused the request. The saved key is not sent to a changed endpoint, so type the key to test it here.",
            "none" => "The endpoint refused the request, and no key was sent. Type a key.",
            "typed" => $"The endpoint did not accept the typed key.{Detail(result)}",
            _ => $"The endpoint did not accept the saved key.{Detail(result)}"
        },
        ModelFailureKind.Timeout => "The endpoint did not answer in time.",
        ModelFailureKind.Unreachable => $"The endpoint could not be reached from this server.{Detail(result)}",
        _ => $"The endpoint did not provide a model list.{Detail(result)}"
    };

    private static string Detail(ModelListResult result) => string.IsNullOrWhiteSpace(result.FailureDetail) ? string.Empty : $" ({result.FailureDetail})";

    private static string Describe(ModelListResult result) => result.Failure switch
    {
        ModelFailureKind.Unauthorized => "The endpoint did not accept the stored key, so the model list could not be read. You can still type a model name.",
        ModelFailureKind.Timeout => "The endpoint did not answer in time, so the model list could not be read. You can still type a model name.",
        ModelFailureKind.Unreachable => "The endpoint could not be reached, so the model list could not be read. You can still type a model name.",
        _ => "The endpoint did not provide a model list. You can still type a model name."
    };
}
