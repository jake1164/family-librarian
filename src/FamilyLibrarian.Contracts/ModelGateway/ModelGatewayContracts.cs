namespace FamilyLibrarian.Contracts.ModelGateway;

/// <param name="Role">A role name such as <c>CandidateAmbiguityAssessment</c>.</param>
/// <param name="Placement"><c>Local</c> or <c>Cloud</c>.</param>
public sealed record ModelRoleDto(string Role, bool IsEnabled, string Placement, string? LocalModel, string? CloudModel);

/// <summary>The gateway settings. The API key is never returned, only whether one is stored.</summary>
public sealed record ModelGatewaySettingsResponse(
    bool IsEnabled,
    string? EndpointUrl,
    bool HasApiKey,
    DateTimeOffset? ApiKeySetAtUtc,
    bool AllowCloud,
    IReadOnlyList<ModelRoleDto> Roles);

public sealed record SaveModelGatewaySettingsRequest(string? EndpointUrl, bool AllowCloud, IReadOnlyList<ModelRoleDto> Roles);

public sealed record SetModelGatewayEnabledRequest(bool Enabled);

public sealed record SetModelGatewayApiKeyRequest(string ApiKey);
