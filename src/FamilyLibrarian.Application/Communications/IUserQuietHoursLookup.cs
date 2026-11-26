namespace FamilyLibrarian.Application.Communications;

/// <summary>Which accounts are inside their own quiet-hours window, as the outbound dispatcher needs it.</summary>
public interface IUserQuietHoursLookup
{
    Task<IReadOnlyCollection<Guid>> ListQuietUserIdsAsync(DateTimeOffset utc, CancellationToken cancellationToken);
}
