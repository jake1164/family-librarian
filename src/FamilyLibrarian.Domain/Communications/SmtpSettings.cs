namespace FamilyLibrarian.Domain.Communications;

/// <summary>
/// Administrator-managed configuration for the optional outbound SMTP provider.
/// One row is created on first configuration. The password is always stored
/// protected and is deliberately write-only outside this aggregate.
/// </summary>
public sealed class SmtpSettings
{
    private SmtpSettings()
    {
    }

    public SmtpSettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public bool IsEnabled { get; private set; }

    public string? Host { get; private set; }

    public int? Port { get; private set; }

    public SmtpSecurityMode SecurityMode { get; private set; } = SmtpSecurityMode.StartTls;

    public string? Username { get; private set; }

    public string? ProtectedPassword { get; private set; }

    public int PasswordFormatVersion { get; private set; }

    public DateTimeOffset? PasswordSetAtUtc { get; private set; }

    public string? FromAddress { get; private set; }

    public string? FromName { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool HasPassword => !string.IsNullOrWhiteSpace(ProtectedPassword);

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetSettings(
        string? host,
        int? port,
        SmtpSecurityMode securityMode,
        string? username,
        string? fromAddress,
        string? fromName,
        Guid? actorUserId,
        DateTimeOffset updatedAtUtc)
    {
        Host = Trim(host);
        Port = port;
        SecurityMode = securityMode;
        Username = Trim(username);
        FromAddress = Trim(fromAddress);
        FromName = Trim(fromName);
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetPassword(string protectedValue, int formatVersion, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("A protected value is required.", nameof(protectedValue));
        }

        ProtectedPassword = protectedValue;
        PasswordFormatVersion = formatVersion;
        PasswordSetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearPassword(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedPassword = null;
        PasswordFormatVersion = 0;
        PasswordSetAtUtc = null;
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

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
