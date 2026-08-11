namespace FamilyLibrarian.Application.Integrations;

/// <summary>Records an administrative action. Detail payloads must be secret-free.</summary>
public interface IAuditWriter
{
    Task WriteAsync(
        string action,
        string subjectType,
        string? subjectId,
        object? detail,
        CancellationToken cancellationToken);
}
