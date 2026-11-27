using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Setup;

namespace FamilyLibrarian.Application.Setup;

/// <summary>Persistence for the single <see cref="SetupGuidanceSettings"/> row.</summary>
public interface ISetupGuidanceStore
{
    Task<SetupGuidanceSettings?> FindAsync(CancellationToken cancellationToken);

    Task<SetupGuidanceSettings> GetOrCreateAsync(CancellationToken cancellationToken);

    /// <returns>
    /// <see langword="false"/> when another save got there first. Nothing was written and the stale copy has been
    /// discarded, so reading again and reapplying the change is safe.
    /// </returns>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}

public static class SetupGuidanceDefaults
{
    /// <summary>The server people sign in to: the saved name, otherwise the server part of the bot's ID.</summary>
    public static string? EffectiveServerName(SetupGuidanceSettings? guidance, MatrixSettings? matrix) =>
        guidance?.MessagingServerName ?? MatrixUserIds.ServerNameOf(matrix?.BotUserId);
}
