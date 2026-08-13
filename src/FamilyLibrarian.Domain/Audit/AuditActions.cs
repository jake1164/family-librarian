namespace FamilyLibrarian.Domain.Audit;

public static class AuditActions
{
    public const string MetadataProviderEnabled = "metadata_provider.enabled";
    public const string MetadataProviderDisabled = "metadata_provider.disabled";
    public const string MetadataProviderCredentialSet = "metadata_provider.credential_set";
    public const string MetadataProviderCredentialCleared = "metadata_provider.credential_cleared";
    public const string MetadataProviderTested = "metadata_provider.tested";

    public const string InvitationCreated = "invitation.created";
    public const string InvitationRevoked = "invitation.revoked";
    public const string InvitationRedeemed = "invitation.redeemed";

    public const string AccountStatusChanged = "account.status_changed";
    public const string AccountAdminGranted = "account.admin_granted";
    public const string AccountAdminRevoked = "account.admin_revoked";
    public const string AccountPasswordReset = "account.password_reset";

    public const string BookRequestStatusChanged = "book_request.status_changed";
    public const string BookRequestNoteChanged = "book_request.note_changed";
}

public static class AuditSubjectTypes
{
    public const string MetadataProvider = "metadata_provider";
    public const string Invitation = "invitation";
    public const string Account = "account";
    public const string BookRequest = "book_request";
}
