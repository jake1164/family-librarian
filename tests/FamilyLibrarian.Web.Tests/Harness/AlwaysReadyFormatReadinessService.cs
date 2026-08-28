using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// The default <see cref="IFormatReadinessService"/> for the ordinary test
/// suite.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AlwaysCleanTestMalwareScanner"/>: an everyday test
/// (admin workflow, acquisition, external providers) must never depend on a
/// configured-and-tested CWA or Audiobookshelf destination just to create a
/// request. A test that specifically exercises the readiness gate itself
/// overrides this via configureTestServices.
/// </remarks>
internal sealed class AlwaysReadyFormatReadinessService : IFormatReadinessService
{
    public Task<FormatReadiness> CheckAsync(RequestMediaType mediaType, CancellationToken cancellationToken) =>
        Task.FromResult(FormatReadiness.Ready);
}
