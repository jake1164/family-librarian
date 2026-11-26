namespace FamilyLibrarian.Domain.Conversation;

/// <summary>
/// The administrator's choice of where the chat assistant is available. One row, created the first time a choice is saved; until then
/// the configured defaults apply. Once it exists it wins over configuration, so what the settings page shows is what is running.
/// </summary>
public sealed class ChatSettings
{
    /// <summary>The only row's key, so a second row cannot be created.</summary>
    public static readonly Guid SingletonId = new("0b6f2a3e-5d1c-4a8e-9c47-2f5e8d1a6b90");

    private ChatSettings()
    {
    }

    public ChatSettings(bool webEnabled, bool matrixEnabled, Guid? actorUserId, DateTimeOffset atUtc)
    {
        CreatedAtUtc = atUtc;
        Set(webEnabled, matrixEnabled, actorUserId, atUtc);
    }

    public Guid Id { get; private set; } = SingletonId;

    public bool WebEnabled { get; private set; }

    public bool MatrixEnabled { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Set(bool webEnabled, bool matrixEnabled, Guid? actorUserId, DateTimeOffset atUtc)
    {
        WebEnabled = webEnabled;
        MatrixEnabled = matrixEnabled;
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = atUtc;
    }
}
