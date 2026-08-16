namespace FamilyLibrarian.Contracts.Policy;

public sealed record PolicyProfileResponse(string Id, string DisplayName, string Description);

public sealed record AcquisitionPolicySettingsResponse(string DefaultProfileId, DateTimeOffset? UpdatedAtUtc);

public sealed record SetDefaultPolicyProfileRequest(string ProfileId);

public sealed record RecommendationResponse(string ProviderId, string ProviderResultId, string ProfileId, string Reason);
