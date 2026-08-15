namespace FamilyLibrarian.Domain.Audit;

public static class AuditActions
{
    public const string ProviderEnabled = "provider.enabled";
    public const string ProviderDisabled = "provider.disabled";
    public const string ProviderCredentialSet = "provider.credential_set";
    public const string ProviderCredentialCleared = "provider.credential_cleared";
    public const string ProviderTested = "provider.tested";

    public const string InvitationCreated = "invitation.created";
    public const string InvitationRevoked = "invitation.revoked";
    public const string InvitationRedeemed = "invitation.redeemed";

    public const string AccountStatusChanged = "account.status_changed";
    public const string AccountAdminGranted = "account.admin_granted";
    public const string AccountAdminRevoked = "account.admin_revoked";
    public const string AccountPasswordReset = "account.password_reset";

    public const string BookRequestStatusChanged = "book_request.status_changed";
    public const string BookRequestNoteChanged = "book_request.note_changed";

    public const string ManualImportStaged = "manual_import.staged";
    public const string ManualImportRejectedNoScanner = "manual_import.rejected_no_scanner";

    public const string AssetEvaluated = "asset.evaluated";
    public const string AssetApproved = "asset.approved";
    public const string AssetRejected = "asset.rejected";
}

public static class AuditSubjectTypes
{
    public const string Provider = "provider";
    public const string Invitation = "invitation";
    public const string Account = "account";
    public const string BookRequest = "book_request";
    public const string MediaAsset = "media_asset";
}
