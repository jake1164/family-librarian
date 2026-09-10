namespace FamilyLibrarian.Domain.Publishing;

/// <summary>
/// Administrator-managed configuration for the CWA (Calibre-Web-Automated)
/// ebook-library destination.
/// </summary>
/// <remarks>
/// One row, created on first configuration — there is exactly one CWA
/// destination in this deployment, not a registry of many. Every secret field
/// follows <c>ProviderSetting</c>'s shape (protected value + format version +
/// last-4-chars hint + set-at) independently, since this entity holds more
/// than one secret: an SFTP private key, its optional passphrase, and the
/// OPDS catalog password. None of these are ever returned to the client —
/// only their hints and set-at timestamps are.
/// </remarks>
public sealed class CwaSettings
{
    private CwaSettings()
    {
    }

    public CwaSettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        IsEnabled = false;
        TransportMode = CwaTransportMode.Local;
        SftpAuthenticationMode = CwaSftpAuthenticationMode.PrivateKey;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public bool IsEnabled { get; private set; }

    public CwaTransportMode TransportMode { get; private set; }

    public string? LocalIngestPath { get; private set; }

    public string? SftpHost { get; private set; }

    public int? SftpPort { get; private set; }

    public string? SftpUsername { get; private set; }

    public string? SftpIngestPath { get; private set; }

    public CwaSftpAuthenticationMode SftpAuthenticationMode { get; private set; }

    public string? ProtectedSftpPrivateKey { get; private set; }

    public int SftpPrivateKeyFormatVersion { get; private set; }

    public string? SftpPrivateKeyHint { get; private set; }

    public DateTimeOffset? SftpPrivateKeySetAtUtc { get; private set; }

    public string? ProtectedSftpPassphrase { get; private set; }

    public int SftpPassphraseFormatVersion { get; private set; }

    public string? SftpPassphraseHint { get; private set; }

    public DateTimeOffset? SftpPassphraseSetAtUtc { get; private set; }

    public string? ProtectedSftpPassword { get; private set; }

    public int SftpPasswordFormatVersion { get; private set; }

    public string? SftpPasswordHint { get; private set; }

    public DateTimeOffset? SftpPasswordSetAtUtc { get; private set; }

    public string? SftpHostKeyFingerprint { get; private set; }

    public DateTimeOffset? SftpHostKeyTrustedAtUtc { get; private set; }

    public string? OpdsBaseUrl { get; private set; }

    /// <summary>
    /// The browser-reachable CWA URL shown to family members (the nav "CWA
    /// library" link and each owned book's deep link) -- <c>null</c> falls
    /// back to <see cref="OpdsBaseUrl"/>. Distinct because <see cref="OpdsBaseUrl"/>
    /// is what Family Librarian's own backend connects to and, in a
    /// containerized deployment, is routinely a Docker-internal hostname
    /// (e.g. <c>http://cwa:8083</c>) a family member's browser cannot resolve.
    /// </summary>
    public string? PublicUrl { get; private set; }

    public string? OpdsUsername { get; private set; }

    public string? ProtectedOpdsPassword { get; private set; }

    public int OpdsPasswordFormatVersion { get; private set; }

    public string? OpdsPasswordHint { get; private set; }

    public DateTimeOffset? OpdsPasswordSetAtUtc { get; private set; }

    /// <summary>
    /// The dedicated CWA account Family Librarian signs in as to invoke CWA's
    /// own e-reader "send to device" web route on a family member's behalf --
    /// independent of the OPDS catalog credential above, and independent of
    /// whether ingest/OPDS are enabled, since it is a separate capability.
    /// </summary>
    public string? EreaderServiceAccountUsername { get; private set; }

    public string? ProtectedEreaderServiceAccountPassword { get; private set; }

    public int EreaderServiceAccountPasswordFormatVersion { get; private set; }

    public string? EreaderServiceAccountPasswordHint { get; private set; }

    public DateTimeOffset? EreaderServiceAccountPasswordSetAtUtc { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool HasSftpPrivateKey => !string.IsNullOrEmpty(ProtectedSftpPrivateKey);

    public bool HasSftpPassphrase => !string.IsNullOrEmpty(ProtectedSftpPassphrase);

    public bool HasSftpPassword => !string.IsNullOrEmpty(ProtectedSftpPassword);

    public bool HasOpdsPassword => !string.IsNullOrEmpty(ProtectedOpdsPassword);

    public bool HasEreaderServiceAccountPassword => !string.IsNullOrEmpty(ProtectedEreaderServiceAccountPassword);

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetSettings(
        CwaTransportMode transportMode,
        string? localIngestPath,
        string? sftpHost,
        int? sftpPort,
        string? sftpUsername,
        string? sftpIngestPath,
        CwaSftpAuthenticationMode sftpAuthenticationMode,
        string? opdsBaseUrl,
        string? publicUrl,
        string? opdsUsername,
        string? ereaderServiceAccountUsername,
        Guid? actorUserId,
        DateTimeOffset updatedAtUtc)
    {
        var normalizedSftpHost = Trim(sftpHost);
        var sftpEndpointChanged = !string.Equals(SftpHost, normalizedSftpHost, StringComparison.OrdinalIgnoreCase)
            || SftpPort != sftpPort;

        TransportMode = transportMode;
        LocalIngestPath = Trim(localIngestPath);
        SftpHost = normalizedSftpHost;
        SftpPort = sftpPort;
        SftpUsername = Trim(sftpUsername);
        SftpIngestPath = Trim(sftpIngestPath);
        SftpAuthenticationMode = sftpAuthenticationMode;
        OpdsBaseUrl = Trim(opdsBaseUrl);
        PublicUrl = Trim(publicUrl);
        OpdsUsername = Trim(opdsUsername);
        EreaderServiceAccountUsername = Trim(ereaderServiceAccountUsername);
        if (sftpEndpointChanged)
        {
            ClearSftpHostKeyTrust();
        }

        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetSftpPrivateKey(
        string protectedValue, int formatVersion, string? hint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        RequireProtectedValue(protectedValue);
        ProtectedSftpPrivateKey = protectedValue;
        SftpPrivateKeyFormatVersion = formatVersion;
        SftpPrivateKeyHint = hint;
        SftpPrivateKeySetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearSftpPrivateKey(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedSftpPrivateKey = null;
        SftpPrivateKeyFormatVersion = 0;
        SftpPrivateKeyHint = null;
        SftpPrivateKeySetAtUtc = null;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetSftpPassphrase(
        string protectedValue, int formatVersion, string? hint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        RequireProtectedValue(protectedValue);
        ProtectedSftpPassphrase = protectedValue;
        SftpPassphraseFormatVersion = formatVersion;
        SftpPassphraseHint = hint;
        SftpPassphraseSetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearSftpPassphrase(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedSftpPassphrase = null;
        SftpPassphraseFormatVersion = 0;
        SftpPassphraseHint = null;
        SftpPassphraseSetAtUtc = null;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetSftpPassword(
        string protectedValue, int formatVersion, string? hint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        RequireProtectedValue(protectedValue);
        ProtectedSftpPassword = protectedValue;
        SftpPasswordFormatVersion = formatVersion;
        SftpPasswordHint = hint;
        SftpPasswordSetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearSftpPassword(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedSftpPassword = null;
        SftpPasswordFormatVersion = 0;
        SftpPasswordHint = null;
        SftpPasswordSetAtUtc = null;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void TrustSftpHostKey(string fingerprint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("An SSH host-key fingerprint is required.", nameof(fingerprint));
        }

        SftpHostKeyFingerprint = fingerprint.Trim();
        SftpHostKeyTrustedAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetOpdsPassword(
        string protectedValue, int formatVersion, string? hint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        RequireProtectedValue(protectedValue);
        ProtectedOpdsPassword = protectedValue;
        OpdsPasswordFormatVersion = formatVersion;
        OpdsPasswordHint = hint;
        OpdsPasswordSetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearOpdsPassword(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedOpdsPassword = null;
        OpdsPasswordFormatVersion = 0;
        OpdsPasswordHint = null;
        OpdsPasswordSetAtUtc = null;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetEreaderServiceAccountPassword(
        string protectedValue, int formatVersion, string? hint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        RequireProtectedValue(protectedValue);
        ProtectedEreaderServiceAccountPassword = protectedValue;
        EreaderServiceAccountPasswordFormatVersion = formatVersion;
        EreaderServiceAccountPasswordHint = hint;
        EreaderServiceAccountPasswordSetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearEreaderServiceAccountPassword(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedEreaderServiceAccountPassword = null;
        EreaderServiceAccountPasswordFormatVersion = 0;
        EreaderServiceAccountPasswordHint = null;
        EreaderServiceAccountPasswordSetAtUtc = null;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void RecordTestResult(bool succeeded, string? message, Guid? actorUserId, DateTimeOffset testedAtUtc)
    {
        LastTestedAtUtc = testedAtUtc;
        LastTestSucceeded = succeeded;
        LastTestMessage = Truncate(message, 512);
        Touch(actorUserId, testedAtUtc);
    }

    private void ResetTestResult()
    {
        LastTestedAtUtc = null;
        LastTestSucceeded = null;
        LastTestMessage = null;
    }

    private void ClearSftpHostKeyTrust()
    {
        SftpHostKeyFingerprint = null;
        SftpHostKeyTrustedAtUtc = null;
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static void RequireProtectedValue(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("A protected value is required.", nameof(protectedValue));
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
