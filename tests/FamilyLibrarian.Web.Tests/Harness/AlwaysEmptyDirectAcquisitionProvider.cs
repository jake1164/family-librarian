using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe direct-acquisition fake: no real network call ever happens in the ordinary test suite.</summary>
internal sealed class AlwaysEmptyDirectAcquisitionProvider : IDirectAcquisitionProvider
{
    public string Id => "gutendex";

    public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);

    public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
        FulfillmentOption fulfillmentOption, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No option was ever returned to fetch.");
}
