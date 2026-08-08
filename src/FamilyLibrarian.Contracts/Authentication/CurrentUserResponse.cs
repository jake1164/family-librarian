namespace FamilyLibrarian.Contracts.Authentication;

public sealed record CurrentUserResponse(
    Guid Id,
    string DisplayName,
    string? Email,
    IReadOnlyList<string> Roles);
