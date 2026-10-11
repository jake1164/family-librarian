namespace FamilyLibrarian.Domain.ModelGateway;

/// <summary>
/// Administrator-managed configuration of the production model gateway (alpha.6 M4-0). One row, created on first
/// configuration. Everything is off and empty by default: nothing here names a real endpoint, credential or model alias, and a
/// role runs only when the gateway, the role and (for a cloud placement) the cloud switch are all deliberately on.
/// The API key is stored protected and is write-only outside this aggregate.
/// </summary>
/// <remarks>
/// The gateway only supplies advisory output. Nothing it configures can make Family Librarian accept a match, approve a
/// request, relax a security hold, acquire or publish anything.
/// </remarks>
public sealed class ModelGatewaySettings
{
    public const int MaxModelNameLength = 128;
    public const int MaxEndpointLength = 512;

    private readonly List<ModelRoleSetting> roles = [];

    private ModelGatewaySettings()
    {
    }

    public ModelGatewaySettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Master switch. When off, no role can run regardless of its own setting.</summary>
    public bool IsEnabled { get; private set; }

    public string? EndpointUrl { get; private set; }

    public string? ProtectedApiKey { get; private set; }

    public int ApiKeyFormatVersion { get; private set; }

    public DateTimeOffset? ApiKeySetAtUtc { get; private set; }

    /// <summary>Explicit opt-in for any role placed on a cloud alias. Off by default; never implied by a fallback.</summary>
    public bool AllowCloud { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyCollection<ModelRoleSetting> Roles => roles;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ProtectedApiKey);

    public ModelRoleSetting? FindRole(ModelRole role) => roles.FirstOrDefault(setting => setting.Role == role);

    public void Configure(string? endpointUrl, bool allowCloud, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        EndpointUrl = Clean(endpointUrl, MaxEndpointLength)?.TrimEnd('/');
        AllowCloud = allowCloud;
        Touch(actorUserId, updatedAtUtc);
    }

    public void ConfigureRole(
        ModelRole role, bool isEnabled, ModelPlacement placement, string? localModel, string? cloudModel,
        Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        var setting = FindRole(role);
        if (setting is null)
        {
            setting = new ModelRoleSetting(Id, role);
            roles.Add(setting);
        }

        setting.Configure(isEnabled, placement, Clean(localModel, MaxModelNameLength), Clean(cloudModel, MaxModelNameLength));
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetApiKey(string protectedValue, int formatVersion, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("A protected value is required.", nameof(protectedValue));
        }

        ProtectedApiKey = protectedValue;
        ApiKeyFormatVersion = formatVersion;
        ApiKeySetAtUtc = updatedAtUtc;
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearApiKey(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedApiKey = null;
        ApiKeyFormatVersion = 0;
        ApiKeySetAtUtc = null;
        Touch(actorUserId, updatedAtUtc);
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

/// <summary>One role's switch, placement and aliases. A role that was never configured behaves as off.</summary>
public sealed class ModelRoleSetting
{
    private ModelRoleSetting()
    {
    }

    internal ModelRoleSetting(Guid settingsId, ModelRole role)
    {
        SettingsId = settingsId;
        Role = role;
    }

    public Guid SettingsId { get; private set; }

    public ModelRole Role { get; private set; }

    public bool IsEnabled { get; private set; }

    public ModelPlacement Placement { get; private set; } = ModelPlacement.Local;

    public string? LocalModel { get; private set; }

    public string? CloudModel { get; private set; }

    /// <summary>The alias this role would call, by its chosen placement only. Never the other one.</summary>
    public string? ActiveModel => Placement == ModelPlacement.Cloud ? CloudModel : LocalModel;

    internal void Configure(bool isEnabled, ModelPlacement placement, string? localModel, string? cloudModel)
    {
        IsEnabled = isEnabled;
        Placement = placement;
        LocalModel = localModel;
        CloudModel = cloudModel;
    }
}
