using FamilyLibrarian.Domain.Conversation;

namespace FamilyLibrarian.Application.Conversation;

/// <summary>Where the chat assistant is switched on right now.</summary>
public sealed record ConversationAvailability(bool Web, bool Matrix)
{
    public bool IsEnabledFor(string transport) => transport switch
    {
        ConversationTransports.Web => Web,
        ConversationTransports.Matrix => Matrix,
        _ => false
    };
}

public interface IChatSettingsStore
{
    Task<ChatSettings?> FindAsync(CancellationToken cancellationToken);

    void Add(ChatSettings settings);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The one place that answers "is chat on?". Read on every use and never cached, so a change on the settings page applies to the next
/// message without a restart.
/// </summary>
public interface IConversationAvailability
{
    Task<ConversationAvailability> GetAsync(CancellationToken cancellationToken);
}

public sealed class ConversationAvailabilityReader(IChatSettingsStore store, ConversationOptions defaults) : IConversationAvailability
{
    public async Task<ConversationAvailability> GetAsync(CancellationToken cancellationToken)
    {
        var saved = await store.FindAsync(cancellationToken);
        return saved is null
            ? new ConversationAvailability(
                defaults.IsEnabledFor(ConversationTransports.Web), defaults.IsEnabledFor(ConversationTransports.Matrix))
            : new ConversationAvailability(saved.WebEnabled, saved.MatrixEnabled);
    }
}
