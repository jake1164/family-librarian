using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Setup;

namespace FamilyLibrarian.Application.Setup;

/// <param name="WizardSeenAtUtc">
/// <see langword="null"/> until this account has been through the first-sign-in check. Accounts that existed before
/// the setup wizard are marked as seen by the migration that added it.
/// </param>
public sealed record UserSetupState(DateTimeOffset? WizardSeenAtUtc, SetupSteps Completed, SetupSteps Skipped);

public interface IUserSetupStateStore
{
    /// <summary><see langword="null"/> when the account no longer exists.</summary>
    Task<UserSetupState?> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> SetAsync(Guid userId, UserSetupState state, CancellationToken cancellationToken);
}

/// <summary>
/// A household member's own first-time setup: which personal settings are still unset, and what to tell them while
/// they set each one. Only ever reads or changes the caller's account.
/// </summary>
/// <remarks>
/// Setting something up is done through the same services as the settings pages. This service only reports on it and
/// remembers which steps the person finished or skipped, so they are not asked again. It records no audit entries
/// because nothing it stores changes what the account can do.
/// </remarks>
public sealed class UserSetupService(
    ICurrentUser currentUser,
    IUserSetupStateStore states,
    IFormatReadinessService formatReadiness,
    DeliveryTargetService deliveryTargets,
    MatrixIdentityLinkService messagingLinks,
    ISetupGuidanceStore guidanceStore,
    IMatrixSettingsStore matrixSettingsStore,
    IConversationAvailability conversationAvailability,
    IClock clock)
{
    private static readonly SetupSteps[] AllSteps = [SetupSteps.Formats, SetupSteps.Kindle, SetupSteps.Messaging];

    public async Task<UserSetupOverview?> GetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId ||
            await states.GetAsync(userId, cancellationToken) is not { } state)
        {
            return null;
        }

        // With ebooks alone there is nothing to choose: both preferences are about audiobooks.
        // Only the switch: a full readiness check would call the malware scanner on every page that asks.
        var formatsAvailable =
            await formatReadiness.IsOfferedAsync(RequestMediaType.Audiobook, cancellationToken);
        var kindleAvailable = await deliveryTargets.IsKindleDeliveryAvailableAsync(cancellationToken);
        var kindleSet = await deliveryTargets.GetMyKindleTargetAsync(cancellationToken) is not null;
        var link = await messagingLinks.GetStatusAsync(cancellationToken);

        var steps = AllSteps.Select(step =>
        {
            var (available, isSet) = step switch
            {
                SetupSteps.Formats => (formatsAvailable, state.Completed.HasFlag(SetupSteps.Formats)),
                SetupSteps.Kindle => (kindleAvailable, kindleSet),
                _ => (link?.IsAvailable == true, link?.IsVerified == true)
            };
            return new UserSetupStep(
                step, available, isSet, state.Completed.HasFlag(step), state.Skipped.HasFlag(step));
        }).ToArray();

        var guidance = await guidanceStore.FindAsync(cancellationToken);
        return new UserSetupOverview(
            state.WizardSeenAtUtc is not null,
            steps,
            kindleAvailable
                ? new KindleGuidance(
                    guidance?.KindleSenderAddress,
                    AmazonStore.FindAddressUrl(guidance?.AmazonStoreDomain),
                    AmazonStore.ManageContentUrl(guidance?.AmazonStoreDomain))
                : null,
            link?.IsAvailable == true ? await MessagingGuidanceAsync(guidance, cancellationToken) : null);
    }

    public Task<bool> MarkWizardSeenAsync(CancellationToken cancellationToken) =>
        UpdateAsync(
            state => state.WizardSeenAtUtc is null ? state with { WizardSeenAtUtc = clock.UtcNow } : state,
            cancellationToken);

    public Task<bool> CompleteAsync(SetupSteps step, CancellationToken cancellationToken) =>
        UpdateAsync(
            state => state with { Completed = state.Completed | step, Skipped = state.Skipped & ~step },
            cancellationToken);

    /// <summary>Skipping also dismisses the reminder. A step already finished stays finished.</summary>
    public Task<bool> SkipAsync(SetupSteps steps, CancellationToken cancellationToken) =>
        UpdateAsync(state => state with { Skipped = state.Skipped | (steps & ~state.Completed) }, cancellationToken);

    private async Task<MessagingGuidance> MessagingGuidanceAsync(
        SetupGuidanceSettings? guidance, CancellationToken cancellationToken)
    {
        var matrix = await matrixSettingsStore.FindAsync(cancellationToken);
        return new MessagingGuidance(
            SetupGuidanceDefaults.EffectiveServerName(guidance, matrix),
            matrix?.BotUserId,
            guidance?.MessagingSignInInstructions,
            guidance?.MessagingHelpUrl,
            guidance?.MessagingWebAppUrl,
            guidance?.ShowElementHelp ?? true,
            (await conversationAvailability.GetAsync(cancellationToken)).Matrix);
    }

    private async Task<bool> UpdateAsync(
        Func<UserSetupState, UserSetupState> change, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId ||
            await states.GetAsync(userId, cancellationToken) is not { } state)
        {
            return false;
        }

        var changed = change(state);
        return changed == state || await states.SetAsync(userId, changed, cancellationToken);
    }
}

public sealed record UserSetupStep(
    SetupSteps Step, bool IsAvailable, bool IsSet, bool IsCompleted, bool IsSkipped)
{
    /// <summary>Worth offering: it can be set up, and has been neither set up nor turned down.</summary>
    public bool IsPending => IsAvailable && !IsSet && !IsCompleted && !IsSkipped;
}

/// <param name="Kindle">Null while Kindle delivery is not set up.</param>
/// <param name="Messaging">Null while messaging is not set up.</param>
public sealed record UserSetupOverview(
    bool WizardSeen,
    IReadOnlyList<UserSetupStep> Steps,
    KindleGuidance? Kindle,
    MessagingGuidance? Messaging)
{
    /// <summary>True only for an account that has never been checked and has something worth offering.</summary>
    public bool ShouldOpenWizard => !WizardSeen && Steps.Any(step => step.IsPending);

    /// <summary>
    /// What a reminder may mention. Formats are left out: they always have a working default.
    /// </summary>
    public IReadOnlyList<SetupSteps> ReminderSteps =>
        Steps.Where(step => step.IsPending && step.Step != SetupSteps.Formats).Select(step => step.Step).ToArray();
}

public sealed record KindleGuidance(string? SenderAddress, string FindAddressUrl, string ManageContentUrl);

/// <param name="ChatEnabled">Whether people can ask for books by message, not only receive updates.</param>
public sealed record MessagingGuidance(
    string? ServerName,
    string? BotUserId,
    string? SignInInstructions,
    string? HelpUrl,
    string? WebAppUrl,
    bool ShowElementHelp,
    bool ChatEnabled);
