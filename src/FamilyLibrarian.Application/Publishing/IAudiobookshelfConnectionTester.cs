using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

public interface IAudiobookshelfConnectionTester
{
    Task<ConnectionTestOutcome> TestAsync(AudiobookshelfSettings settings, CancellationToken cancellationToken);
}
