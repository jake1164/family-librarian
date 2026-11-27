using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe Audiobookshelf connection-test fake: no ordinary test depends on a reachable server.</summary>
internal sealed class AlwaysSucceedsAudiobookshelfConnectionTester : IAudiobookshelfConnectionTester
{
    public Task<ConnectionTestOutcome> TestAsync(AudiobookshelfSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult(new ConnectionTestOutcome(true, "Test double: always succeeds."));
}
