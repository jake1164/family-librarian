namespace FamilyLibrarian.Contracts.Requests;

public sealed record AcquisitionPreferenceResponse(Guid Id, string Kind, string? Value, DateTimeOffset CreatedAtUtc);
