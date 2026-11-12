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

    /// <summary>Records an automated action without attributing it to the current browser user.</summary>
    Task WriteSystemAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken)
        => throw new NotSupportedException("The audit writer must support an explicit system actor.");
}
