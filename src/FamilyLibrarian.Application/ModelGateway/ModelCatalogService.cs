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

    private static string Describe(ModelListResult result) => result.Failure switch
    {
        ModelFailureKind.Unauthorized => "The endpoint did not accept the stored key, so the model list could not be read. You can still type a model name.",
        ModelFailureKind.Timeout => "The endpoint did not answer in time, so the model list could not be read. You can still type a model name.",
        ModelFailureKind.Unreachable => "The endpoint could not be reached, so the model list could not be read. You can still type a model name.",
        _ => "The endpoint did not provide a model list. You can still type a model name."
    };
}
