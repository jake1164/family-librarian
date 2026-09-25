namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Constants for the <c>InteractionGrant</c> authentication scheme (HUMAN-ACQ-1
/// D12): a second, non-default cookie scheme issued only after a Matrix
/// magic-link claim succeeds. It can reach exactly one job's status/view
/// endpoints and can never satisfy the <c>Admin</c> policy — see Microsoft
/// Learn's "Authorize with a specific scheme in ASP.NET Core" for the pattern
/// this follows (a named scheme + a policy scoped to it via <c>AuthenticationSchemes</c>).
/// </summary>
public static class InteractionGrantDefaults
{
    public const string AuthenticationScheme = "InteractionGrant";

    public const string PolicyName = "InteractionGrant";

    public const string CookieName = "fl.interaction";

    /// <summary>Claim carrying the one <see cref="Domain.Acquisition.ProviderAcquisitionJob.Id"/> this grant may reach.</summary>
    public const string JobIdClaimType = "fl:interaction_job";

    /// <summary>Claim carrying the originating <see cref="Domain.Acquisition.ProviderInteractionAlert.Id"/>, if any.</summary>
    public const string AlertIdClaimType = "fl:alert";
}
