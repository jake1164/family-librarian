namespace FamilyLibrarian.Domain.Providers;

/// <summary>
/// An admin-configured repository catalog URL: a discovery listing of
/// available external providers, not an installer.
/// </summary>
/// <remarks>
/// Fetched over HTTPS and cached verbatim as JSON for display. Signature
/// verification is a deliberately deferred hardening step (see the M13 plan)
/// — the trust model for this pass is the same as adding any other
/// integration URL: an administrator chooses which catalogs to add. Nothing
/// here ever creates an <see cref="ExternalProvider"/> row; registering an
/// actual running instance stays a separate, manual step.
/// </remarks>
public sealed class ProviderCatalog
{
    private ProviderCatalog()
    {
    }

    public ProviderCatalog(string url, string? displayName, DateTimeOffset createdAtUtc)
    {
        Url = RequireText(url, nameof(url));
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Url : displayName.Trim();
        IsEnabled = true;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Url { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;

    public bool IsEnabled { get; private set; }

    /// <summary>The raw entries array from the last successful fetch, verbatim.</summary>
    public string? CachedEntriesJson { get; private set; }

    public DateTimeOffset? LastFetchedAtUtc { get; private set; }

    public bool? LastFetchSucceeded { get; private set; }

    public string? LastFetchMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void RecordFetchSucceeded(string entriesJson, Guid? actorUserId, DateTimeOffset fetchedAtUtc)
    {
        CachedEntriesJson = entriesJson;
        LastFetchedAtUtc = fetchedAtUtc;
        LastFetchSucceeded = true;
        LastFetchMessage = null;
        Touch(actorUserId, fetchedAtUtc);
    }

    public void RecordFetchFailed(string message, Guid? actorUserId, DateTimeOffset fetchedAtUtc)
    {
        LastFetchedAtUtc = fetchedAtUtc;
        LastFetchSucceeded = false;
        LastFetchMessage = message.Length <= 512 ? message : message[..512];
        Touch(actorUserId, fetchedAtUtc);
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value.Trim();
    }
}
