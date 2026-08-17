using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Requests;

/// <summary>
/// The narrow request lookup needed by a delivery destination to confirm one
/// requested format became available.
/// </summary>
public interface IBookRequestFulfillmentStore
{
    Task<BookRequest?> FindByFormatIdAsync(Guid requestFormatId, CancellationToken cancellationToken);
}
