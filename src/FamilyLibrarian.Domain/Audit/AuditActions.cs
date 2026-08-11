namespace FamilyLibrarian.Domain.Audit;

public static class AuditActions
{
    public const string MetadataProviderEnabled = "metadata_provider.enabled";
    public const string MetadataProviderDisabled = "metadata_provider.disabled";
    public const string MetadataProviderCredentialSet = "metadata_provider.credential_set";
    public const string MetadataProviderCredentialCleared = "metadata_provider.credential_cleared";
    public const string MetadataProviderTested = "metadata_provider.tested";
}

public static class AuditSubjectTypes
{
    public const string MetadataProvider = "metadata_provider";
}
