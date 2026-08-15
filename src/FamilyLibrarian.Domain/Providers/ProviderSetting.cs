namespace FamilyLibrarian.Domain.Providers;

/// <summary>
/// Administrator-managed state for one installed provider.
/// </summary>
/// <remarks>
/// This record never holds a plaintext credential. <see cref="ProtectedCredential"/>
/// is ciphertext produced by the host's Data Protection key ring, and it is only
/// ever decrypted inside the host process when building an outbound provider
/// request. It is never returned to the WebAssembly client, logged, or written to
/// an audit payload.
/// </remarks>
public sealed class ProviderSetting
{
    private ProviderSetting()
    {
    }

    public ProviderSetting(string providerId, DateTimeOffset createdAtUtc)
    {
        ProviderId = RequireProviderId(providerId);
        IsEnabled = false;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public string ProviderId { get; private set; } = null!;

    public bool IsEnabled { get; private set; }

    /// <summary>Ciphertext, or <c>null</c> when no credential has been stored.</summary>
    public string? ProtectedCredential { get; private set; }

    /// <summary>
    /// Identifies the protection purpose/format used to produce
    /// <see cref="ProtectedCredential"/>, so a later format change can migrate
    /// stored values instead of silently failing to unprotect them.
    /// </summary>
    public int CredentialFormatVersion { get; private set; }

    public DateTimeOffset? CredentialSetAtUtc { get; private set; }

    /// <summary>Last four characters of the credential, for operator recognition only.</summary>
    public string? CredentialHint { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    /// <summary>A short, non-secret description of the last connection test outcome.</summary>
    public string? LastTestMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool HasStoredCredential => !string.IsNullOrEmpty(ProtectedCredential);

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    /// <param name="protectedCredential">Ciphertext. Callers must never pass a plaintext secret.</param>
    /// <param name="credentialHint">Last four plaintext characters, used only for display.</param>
    public void SetCredential(
        string protectedCredential,
        int credentialFormatVersion,
        string? credentialHint,
        Guid? actorUserId,
        DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(protectedCredential))
        {
            throw new ArgumentException(
                "A protected credential value is required.",
                nameof(protectedCredential));
        }

        ProtectedCredential = protectedCredential;
        CredentialFormatVersion = credentialFormatVersion;
        CredentialHint = credentialHint;
        CredentialSetAtUtc = updatedAtUtc;

        // A replaced credential invalidates any previous test outcome.
        LastTestedAtUtc = null;
        LastTestSucceeded = null;
        LastTestMessage = null;

        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearCredential(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedCredential = null;
        CredentialFormatVersion = 0;
        CredentialHint = null;
        CredentialSetAtUtc = null;
        LastTestedAtUtc = null;
        LastTestSucceeded = null;
        LastTestMessage = null;

        // A provider that requires a credential cannot stay enabled without one.
        // The caller decides whether that applies; disabling here would be wrong
        // for keyless providers, so enablement is left to the application command.
        Touch(actorUserId, updatedAtUtc);
    }

    public void RecordTestResult(
        bool succeeded,
        string? message,
        Guid? actorUserId,
        DateTimeOffset testedAtUtc)
    {
        LastTestedAtUtc = testedAtUtc;
        LastTestSucceeded = succeeded;
        LastTestMessage = Truncate(message, 512);
        Touch(actorUserId, testedAtUtc);
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string RequireProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A provider id is required.", nameof(providerId));
        }

        return providerId.Trim();
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];
}
