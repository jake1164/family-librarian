using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// A household member's own quiet-hours window (HUMAN-ACQ-1 D9) -- self-service,
/// mirroring <see cref="AudiobookNarrationPreferenceService"/>'s shape exactly.
/// A general per-user setting shown to every account, even though only admins
/// receive the one alert that enforces it today.
/// </summary>
public sealed class QuietHoursPreferenceService(
    IUserAccountStore accounts,
    ICurrentUser currentUser,
    IAuditWriter audit)
{
    public async Task<QuietHours?> GetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return null;
        }

        return await accounts.GetQuietHoursAsync(userId, cancellationToken);
    }

    /// <summary>
    /// <paramref name="timeZoneId"/>/<paramref name="startMinute"/>/<paramref name="endMinute"/>
    /// all null clears the window; a partial set is rejected.
    /// </summary>
    public async Task<AccountOperationResult> SetAsync(
        string? timeZoneId, int? startMinute, int? endMinute, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return AccountOperationResult.Failure("You must be signed in.");
        }

        QuietHours? quietHours;
        if (timeZoneId is null && startMinute is null && endMinute is null)
        {
            quietHours = null;
        }
        else if (timeZoneId is null || startMinute is null || endMinute is null)
        {
            return AccountOperationResult.Failure(
                "Time zone, start, and end must all be set together, or all left empty to clear.");
        }
        else
        {
            try
            {
                quietHours = new QuietHours(timeZoneId, startMinute.Value, endMinute.Value);
            }
            catch (ArgumentException exception)
            {
                return AccountOperationResult.Failure(exception.Message);
            }
        }

        var result = await accounts.SetQuietHoursAsync(userId, quietHours, cancellationToken);
        if (result.Succeeded)
        {
            await audit.WriteAsync(
                AuditActions.AccountQuietHoursChanged,
                AuditSubjectTypes.Account,
                userId.ToString(),
                new { UserId = userId, TimeZoneId = quietHours?.TimeZoneId, quietHours?.StartMinute, quietHours?.EndMinute },
                cancellationToken);
        }

        return result;
    }
}
