using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Domain.Conversation;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Conversation;

public sealed class ChatSettingsStore(AppDbContext database) : IChatSettingsStore
{
    public Task<ChatSettings?> FindAsync(CancellationToken cancellationToken) => database.ChatSettings.SingleOrDefaultAsync(cancellationToken);

    public void Add(ChatSettings settings) => database.ChatSettings.Add(settings);

    public Task SaveAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
