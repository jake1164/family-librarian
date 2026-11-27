namespace FamilyLibrarian.Contracts.Setup;

/// <summary>The names used for setup steps in <see cref="UserSetupResponse"/> and the setup routes.</summary>
public static class SetupStepNames
{
    public const string Formats = "Formats";
    public const string Kindle = "Kindle";
    public const string Messaging = "Messaging";
}

/// <param name="ShouldOpenWizard">True only until this account has first been checked, when something is unset.</param>
/// <param name="ReminderSteps">Available features that are neither set up nor skipped.</param>
/// <param name="Kindle">Null while Kindle delivery is not set up. Carries no secret.</param>
/// <param name="Messaging">Null while messaging is not set up. Carries no secret.</param>
public sealed record UserSetupResponse(
    bool WizardSeen,
    bool ShouldOpenWizard,
    IReadOnlyList<UserSetupStepResponse> Steps,
    IReadOnlyList<string> ReminderSteps,
    KindleGuidanceResponse? Kindle,
    MessagingGuidanceResponse? Messaging);

public sealed record UserSetupStepResponse(
    string Step, bool IsAvailable, bool IsSet, bool IsCompleted, bool IsSkipped);

public sealed record KindleGuidanceResponse(string? SenderAddress, string FindAddressUrl, string ManageContentUrl);

public sealed record MessagingGuidanceResponse(
    string? ServerName,
    string? BotUserId,
    string? SignInInstructions,
    string? HelpUrl,
    string? WebAppUrl,
    bool ShowElementHelp,
    bool ChatEnabled);

public sealed record SkipSetupStepsRequest(IReadOnlyList<string> Steps);

public sealed record SetupGuidanceSettingsResponse(
    string? KindleSenderAddress,
    string? AmazonStoreDomain,
    string EffectiveAmazonStoreDomain,
    string FindKindleAddressUrl,
    string ManageContentUrl,
    string? MessagingServerName,
    string? SuggestedServerName,
    string? MessagingSignInInstructions,
    string? MessagingHelpUrl,
    string? MessagingWebAppUrl,
    bool ShowElementHelp);

/// <param name="AmazonStore">A store domain or any link from the store; blank means the default store.</param>
public sealed record SetKindleGuidanceRequest(string? KindleSenderAddress, string? AmazonStore);

public sealed record SetMessagingGuidanceRequest(
    string? ServerName, string? SignInInstructions, string? HelpUrl, string? WebAppUrl);

public sealed record SetShowElementHelpRequest(bool Enabled);
