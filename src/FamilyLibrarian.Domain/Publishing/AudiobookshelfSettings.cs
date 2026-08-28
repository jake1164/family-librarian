namespace FamilyLibrarian.Domain.Publishing;

/// <summary>
/// Administrator-managed configuration for the Audiobookshelf delivery
/// destination. One row, created on first configuration.
/// </summary>
public sealed class AudiobookshelfSettings
{
    private AudiobookshelfSettings()
    {
    }

    public AudiobookshelfSettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        IsEnabled = false;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public bool IsEnabled { get; private set; }

    public string? BaseUrl { get; private set; }

    public string? LibraryId { get; private set; }

    public string? FolderId { get; private set; }

    public string? ProtectedApiToken { get; private set; }

    public int ApiTokenFormatVersion { get; private set; }

    public string? ApiTokenHint { get; private set; }

    public DateTimeOffset? ApiTokenSetAtUtc { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool HasApiToken => !string.IsNullOrEmpty(ProtectedApiToken);

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetSettings(
        string? baseUrl,
        string? libraryId,
        string? folderId,
        Guid? actorUserId,
        DateTimeOffset updatedAtUtc)
    {
        BaseUrl = Trim(baseUrl);
        LibraryId = Trim(libraryId);
        FolderId = Trim(folderId);
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetApiToken(
        string protectedValue, int formatVersion, string? hint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("A protected value is required.", nameof(protectedValue));
        }

        ProtectedApiToken = protectedValue;
        ApiTokenFormatVersion = formatVersion;
        ApiTokenHint = hint;
        ApiTokenSetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearApiToken(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedApiToken = null;
        ApiTokenFormatVersion = 0;
        ApiTokenHint = null;
        ApiTokenSetAtUtc = null;
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
