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

    public string? ProtectedSftpPrivateKey { get; private set; }

    public int SftpPrivateKeyFormatVersion { get; private set; }

    public string? SftpPrivateKeyHint { get; private set; }

    public DateTimeOffset? SftpPrivateKeySetAtUtc { get; private set; }

    public string? ProtectedSftpPassphrase { get; private set; }

    public int SftpPassphraseFormatVersion { get; private set; }

    public string? SftpPassphraseHint { get; private set; }

    public DateTimeOffset? SftpPassphraseSetAtUtc { get; private set; }

    public string? OpdsBaseUrl { get; private set; }

    public string? OpdsUsername { get; private set; }

    public string? ProtectedOpdsPassword { get; private set; }

    public int OpdsPasswordFormatVersion { get; private set; }

    public string? OpdsPasswordHint { get; private set; }

    public DateTimeOffset? OpdsPasswordSetAtUtc { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool HasSftpPrivateKey => !string.IsNullOrEmpty(ProtectedSftpPrivateKey);

    public bool HasSftpPassphrase => !string.IsNullOrEmpty(ProtectedSftpPassphrase);

    public bool HasOpdsPassword => !string.IsNullOrEmpty(ProtectedOpdsPassword);

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
        string? opdsBaseUrl,
        string? opdsUsername,
        Guid? actorUserId,
        DateTimeOffset updatedAtUtc)
    {
        TransportMode = transportMode;
        LocalIngestPath = Trim(localIngestPath);
        SftpHost = Trim(sftpHost);
        SftpPort = sftpPort;
        SftpUsername = Trim(sftpUsername);
        SftpIngestPath = Trim(sftpIngestPath);
        OpdsBaseUrl = Trim(opdsBaseUrl);
        OpdsUsername = Trim(opdsUsername);
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
