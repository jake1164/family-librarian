namespace FamilyLibrarian.Domain.Audit;

public static class AuditActions
{
    public const string ProviderEnabled = "provider.enabled";
    public const string ProviderDisabled = "provider.disabled";
    public const string ProviderCredentialSet = "provider.credential_set";
    public const string ProviderCredentialCleared = "provider.credential_cleared";
    public const string ProviderTested = "provider.tested";
    public const string GutenbergCatalogPurged = "gutenberg_catalog.purged";

    public const string InvitationCreated = "invitation.created";
    public const string InvitationRevoked = "invitation.revoked";
    public const string InvitationRedeemed = "invitation.redeemed";

    public const string AccountStatusChanged = "account.status_changed";
    public const string AccountAdminGranted = "account.admin_granted";
    public const string AccountAdminRevoked = "account.admin_revoked";
    public const string AccountPasswordReset = "account.password_reset";
    public const string AccountAudiobookNarrationPreferenceChanged = "account.audiobook_narration_preference_changed";
    public const string AccountQuietHoursChanged = "account.quiet_hours_changed";

    public const string BookRequestStatusChanged = "book_request.status_changed";
    public const string BookRequestNoteChanged = "book_request.note_changed";

    public const string ManualImportStaged = "manual_import.staged";
    public const string ManualImportRejectedNoScanner = "manual_import.rejected_no_scanner";
    public const string DirectAcquisitionStaged = "direct_acquisition.staged";

    public const string AssetEvaluated = "asset.evaluated";
    public const string AssetEvaluationFailed = "asset.evaluation_failed";
    public const string AssetIdentityVerified = "asset.identity_verified";
    public const string AssetIdentityUnmatched = "asset.identity_unmatched";
    public const string AssetIdentityOverridden = "asset.identity_overridden";
    public const string AssetApproved = "asset.approved";
    public const string AssetRejected = "asset.rejected";
    public const string AssetDestroyed = "asset.destroyed";
    public const string AssetMalwareDestroyed = "asset.malware_destroyed";
    public const string AssetMalwareDestructionFailed = "asset.malware_destruction_failed";
    public const string AssetPublished = "asset.published";
    public const string AssetPublishFailed = "asset.publish_failed";
    public const string AssetMatchAmbiguous = "asset.match_ambiguous";
    public const string AssetArchiveCleanupFailed = "asset.archive_cleanup_failed";

    public const string PublishingDestinationEnabled = "publishing_destination.enabled";
    public const string PublishingDestinationDisabled = "publishing_destination.disabled";
    public const string PublishingDestinationSettingsChanged = "publishing_destination.settings_changed";
    public const string PublishingDestinationSecretSet = "publishing_destination.secret_set";
    public const string PublishingDestinationSecretCleared = "publishing_destination.secret_cleared";
    public const string PublishingDestinationTested = "publishing_destination.tested";

    public const string CommunicationProviderEnabled = "communication_provider.enabled";
    public const string CommunicationProviderDisabled = "communication_provider.disabled";
    public const string CommunicationProviderSettingsChanged = "communication_provider.settings_changed";
    public const string CommunicationProviderSecretSet = "communication_provider.secret_set";
    public const string CommunicationProviderSecretCleared = "communication_provider.secret_cleared";
    public const string CommunicationProviderTested = "communication_provider.tested";

    /// <summary>COMM-1 §C: a household member's own Matrix identity link, distinct from admin provider config above.</summary>
    public const string MatrixIdentityLinkRequested = "matrix_identity_link.requested";
    public const string MatrixIdentityLinkVerified = "matrix_identity_link.verified";
    public const string MatrixIdentityLinkRemoved = "matrix_identity_link.removed";

    public const string AcquisitionPolicyDefaultChanged = "acquisition_policy.default_changed";

    public const string OidcEnabled = "oidc.enabled";
    public const string OidcDisabled = "oidc.disabled";
    public const string OidcSettingsChanged = "oidc.settings_changed";
    public const string OidcSecretSet = "oidc.secret_set";
    public const string OidcSecretCleared = "oidc.secret_cleared";
    public const string OidcTested = "oidc.tested";
    public const string OidcLocalLoginChanged = "oidc.local_login_changed";

    public const string ExternalLoginLinked = "account.external_login_linked";
    public const string ExternalAccountCreated = "account.external_account_created";

    public const string ExternalProviderCreated = "external_provider.created";
    public const string ExternalProviderUpdated = "external_provider.updated";
    public const string ExternalProviderEnabled = "external_provider.enabled";
    public const string ExternalProviderDisabled = "external_provider.disabled";
    public const string ExternalProviderApiKeySet = "external_provider.api_key_set";
    public const string ExternalProviderApiKeyCleared = "external_provider.api_key_cleared";
    public const string ExternalProviderTested = "external_provider.tested";
    public const string ExternalProviderRemoved = "external_provider.removed";
    public const string ExternalProviderRecheckScheduleChanged = "external_provider.recheck_schedule_changed";
    public const string ExternalProviderAutoAcquireEnabled = "external_provider.auto_acquire_enabled";
    public const string ExternalProviderAutoAcquireDisabled = "external_provider.auto_acquire_disabled";
    public const string ExternalProviderAcquisitionModeChanged = "external_provider.acquisition_mode_changed";
    public const string ExternalProviderAcquisitionStaged = "external_provider_acquisition.staged";
    public const string ProviderInteractionStarted = "provider_interaction.started";
    public const string ProviderInteractionFallbackSelected = "provider_interaction.fallback_selected";
    public const string ProviderInteractionCancelled = "provider_interaction.cancelled";
    public const string ProviderInteractionExpired = "provider_interaction.expired";
    public const string ProviderInteractionCompleted = "provider_interaction.completed";
    public const string ProviderInteractionFailed = "provider_interaction.failed";
    public const string ProviderInteractionViewConnected = "provider_interaction.view_connected";
    public const string ProviderInteractionViewEnded = "provider_interaction.view_ended";
    public const string ProviderInteractionClaimed = "provider_interaction.claimed";
    public const string ProviderInteractionTakenOver = "provider_interaction.taken_over";


    public const string ProviderCatalogAdded = "provider_catalog.added";
    public const string ProviderCatalogRemoved = "provider_catalog.removed";
    public const string ProviderCatalogRefreshed = "provider_catalog.refreshed";

    public const string SettingsBackupImported = "settings_backup.imported";

    public const string DeliveryAttemptSubmitted = "delivery_attempt.submitted";
    public const string DeliveryAttemptFailed = "delivery_attempt.failed";
    public const string DeliveryAttemptConfirmed = "delivery_attempt.confirmed";
    public const string DeliveryAttemptReportedMissing = "delivery_attempt.reported_missing";

    public const string DeliveryTargetAdminAddressChanged = "delivery_target.admin_address_changed";
    public const string DeliveryTargetAdminEnabledChanged = "delivery_target.admin_enabled_changed";
}

public static class AuditSubjectTypes
{
    public const string Provider = "provider";
    public const string Invitation = "invitation";
    public const string Account = "account";
    public const string BookRequest = "book_request";
    public const string MediaAsset = "media_asset";
    public const string PublishingDestination = "publishing_destination";
    public const string CommunicationProvider = "communication_provider";
    public const string MatrixIdentityLink = "matrix_identity_link";
    public const string AcquisitionPolicy = "acquisition_policy";
    public const string Oidc = "oidc";
    public const string ExternalProvider = "external_provider";
    public const string ProviderCatalog = "provider_catalog";
    public const string SettingsBackup = "settings_backup";
    public const string DeliveryAttempt = "delivery_attempt";
    public const string DeliveryTarget = "delivery_target";
    public const string ProviderInteraction = "provider_interaction";
}
