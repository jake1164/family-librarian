using System.Text.Json;
using FamilyLibrarian.Contracts.ModelGateway;

namespace FamilyLibrarian.Web.Client.ModelGateway;

/// <summary>
/// The editable state of the AI models page, kept apart from the last settings the host returned so that a host round
/// trip which does not save the form (the master switch, storing or removing the key) cannot silently throw away what
/// the administrator has typed but not yet saved.
/// </summary>
public sealed class ModelGatewayForm
{
    private string savedSnapshot = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public bool AllowCloud { get; set; }

    public bool AllowCloudExcerpts { get; set; }

    public List<RoleEdit> Roles { get; private set; } = [];

    public LimitsEdit Limits { get; private set; } = new();

    /// <summary>True when the form differs from the settings last loaded from, or saved to, the host.</summary>
    public bool HasUnsavedChanges => Snapshot() != savedSnapshot;

    /// <summary>Replaces every field with the host's settings; afterwards there are no unsaved changes.</summary>
    public void Load(ModelGatewaySettingsResponse value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Endpoint = value.EndpointUrl ?? string.Empty;
        AllowCloud = value.AllowCloud;
        AllowCloudExcerpts = value.AllowCloudExcerpts;
        Limits = new LimitsEdit
        {
            TimeoutSeconds = value.Limits.TimeoutSeconds, MaxInputCharacters = value.Limits.MaxInputCharacters,
            MaxOutputTokens = value.Limits.MaxOutputTokens, MaxConcurrentCalls = value.Limits.MaxConcurrentCalls,
            Interactive = BudgetEdit.From(value.Limits.Interactive), Background = BudgetEdit.From(value.Limits.Background),
            CloudDailyRequests = value.Limits.CloudDailyRequests, CloudDailyTokens = value.Limits.CloudDailyTokens
        };
        Roles = [.. value.Roles.Select(role => new RoleEdit
        {
            Role = role.Role, IsEnabled = role.IsEnabled, Placement = role.Placement, LocalModel = role.LocalModel,
            CloudModel = role.CloudModel, Reasoning = role.Reasoning
        })];
        savedSnapshot = Snapshot();
    }

    /// <summary>
    /// Takes the host's settings after a call that did not save the form. With nothing unsaved the form simply follows the
    /// host; with unsaved changes they are kept, still unsaved, until the administrator saves or reloads.
    /// </summary>
    /// <returns>True when unsaved changes were kept.</returns>
    public bool Refresh(ModelGatewaySettingsResponse value)
    {
        if (HasUnsavedChanges)
        {
            return true;
        }

        Load(value);
        return false;
    }

    public SaveModelGatewaySettingsRequest ToRequest() => new(
        Endpoint, AllowCloud,
        [.. Roles.Select(role => new ModelRoleDto(role.Role, role.IsEnabled, role.Placement, role.LocalModel, role.CloudModel, role.Reasoning))],
        new ModelLimitsDto(
            Limits.TimeoutSeconds, Limits.MaxInputCharacters, Limits.MaxOutputTokens, Limits.MaxConcurrentCalls,
            Limits.Interactive.ToDto(), Limits.Background.ToDto(), Limits.CloudDailyRequests, Limits.CloudDailyTokens),
        AllowCloud && AllowCloudExcerpts);

    private string Snapshot() => JsonSerializer.Serialize(ToRequest());

    public sealed class RoleEdit
    {
        public string Role { get; init; } = string.Empty;
        public bool IsEnabled { get; set; }
        public string Placement { get; set; } = "Local";
        public string? LocalModel { get; set; }
        public string? CloudModel { get; set; }
        public string Reasoning { get; set; } = "Inherit";
    }

    public sealed class BudgetEdit
    {
        public int UserDailyRequests { get; set; }
        public int UserDailyTokens { get; set; }
        public int InstallationDailyRequests { get; set; }
        public int InstallationDailyTokens { get; set; }

        internal static BudgetEdit From(ModelBudgetDto budget) => new()
        {
            UserDailyRequests = budget.UserDailyRequests, UserDailyTokens = budget.UserDailyTokens,
            InstallationDailyRequests = budget.InstallationDailyRequests, InstallationDailyTokens = budget.InstallationDailyTokens
        };

        internal ModelBudgetDto ToDto() => new(UserDailyRequests, UserDailyTokens, InstallationDailyRequests, InstallationDailyTokens);
    }

    public sealed class LimitsEdit
    {
        public int TimeoutSeconds { get; set; }
        public int MaxInputCharacters { get; set; }
        public int MaxOutputTokens { get; set; }
        public int MaxConcurrentCalls { get; set; }
        public BudgetEdit Interactive { get; set; } = new();
        public BudgetEdit Background { get; set; } = new();
        public int CloudDailyRequests { get; set; }
        public int CloudDailyTokens { get; set; }
    }
}
