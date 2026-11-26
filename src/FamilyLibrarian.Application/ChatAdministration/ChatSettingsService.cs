using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Conversation;

// Deliberately outside the Conversation namespace: chat code has no administrator authority, and ConversationBoundaryTests pins that.
namespace FamilyLibrarian.Application.ChatAdministration;

/// <param name="MatrixConnectionReady">False while the Matrix bot connection is off, so the page can say Matrix chat cannot answer yet.</param>
public sealed record ChatSettingsView(bool WebEnabled, bool MatrixEnabled, bool MatrixConnectionReady);

/// <summary>The administrator's side of the chat switches. It checks for an administrator itself.</summary>
public sealed class ChatSettingsService(
    IChatSettingsStore store, IConversationAvailability availability, IMatrixSettingsStore matrixSettings, ICurrentUser currentUser,
    ICurrentAdministrator administrator, IAuditWriter audit, IClock clock)
{
    /// <summary>Null when the caller is not an administrator.</summary>
    public async Task<ChatSettingsView?> GetAsync(CancellationToken cancellationToken) =>
        IsAdmin ? await ViewAsync(cancellationToken) : null;

    /// <summary>
    /// Saves the switches that are given and leaves the others as they are, so a page that has been open for a while cannot undo a
    /// change to a switch it did not touch. Null when the caller is not an administrator.
    /// </summary>
    public async Task<ChatSettingsView?> UpdateAsync(bool? webEnabled, bool? matrixEnabled, CancellationToken cancellationToken)
    {
        if (!IsAdmin) return null;

        var current = await availability.GetAsync(cancellationToken);
        var web = webEnabled ?? current.Web;
        var matrix = matrixEnabled ?? current.Matrix;

        var row = await store.FindAsync(cancellationToken);
        if (row is null)
        {
            store.Add(new ChatSettings(web, matrix, currentUser.UserId, clock.UtcNow));
        }
        else
        {
            row.Set(web, matrix, currentUser.UserId, clock.UtcNow);
        }

        await store.SaveAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ChatSettingsChanged, AuditSubjectTypes.ChatSettings, null,
            new { WebEnabled = web, MatrixEnabled = matrix, ActorUserId = currentUser.UserId }, cancellationToken);
        return await ViewAsync(cancellationToken);
    }

    private async Task<ChatSettingsView> ViewAsync(CancellationToken cancellationToken)
    {
        var current = await availability.GetAsync(cancellationToken);
        var connection = await matrixSettings.FindAsync(cancellationToken);
        return new ChatSettingsView(current.Web, current.Matrix, connection is { IsEnabled: true, HasAccessToken: true });
    }

    private bool IsAdmin => currentUser.UserId is not null && administrator.IsAdministrator;
}
