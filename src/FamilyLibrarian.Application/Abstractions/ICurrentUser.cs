namespace FamilyLibrarian.Application.Abstractions;

/// <summary>The authenticated caller, for ownership and audit attribution.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    string? DisplayName { get; }
}
