namespace FamilyLibrarian.Domain.Communications;

/// <summary>
/// Administrator-managed configuration for the optional outbound Matrix
/// provider (COMM-1). One row is created on first configuration, the same
/// singleton-aggregate shape as <see cref="SmtpSettings"/>. The bot's access
/// token is always stored protected and is deliberately write-only outside
/// this aggregate.
/// </summary>
public sealed class MatrixSettings
{
    private MatrixSettings()
    {
    }

    public MatrixSettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public bool IsEnabled { get; private set; }

    public string? HomeserverUrl { get; private set; }

    public string? BotUserId { get; private set; }

    public string? ProtectedAccessToken { get; private set; }

    public int AccessTokenFormatVersion { get; private set; }

    public DateTimeOffset? AccessTokenSetAtUtc { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    /// <summary>
    /// The Matrix <c>/sync</c> "since" cursor for the inbound poll (COMM-1
    /// §D). Purely operational bookkeeping -- unlike every other field here,
    /// updating it is not an administrator action and never resets the test
    /// result or touches <see cref="UpdatedByUserId"/>/<see cref="UpdatedAtUtc"/>.
    /// </summary>
    public string? LastSyncToken { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool HasAccessToken => !string.IsNullOrWhiteSpace(ProtectedAccessToken);

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetSettings(string? homeserverUrl, string? botUserId, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        HomeserverUrl = Trim(homeserverUrl)?.TrimEnd('/');
        BotUserId = Trim(botUserId);
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetAccessToken(string protectedValue, int formatVersion, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("A protected value is required.", nameof(protectedValue));
        }

        ProtectedAccessToken = protectedValue;
        AccessTokenFormatVersion = formatVersion;
        AccessTokenSetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearAccessToken(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedAccessToken = null;
        AccessTokenFormatVersion = 0;
        AccessTokenSetAtUtc = null;
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

    public void RecordSyncToken(string? nextBatch) => LastSyncToken = nextBatch;

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
