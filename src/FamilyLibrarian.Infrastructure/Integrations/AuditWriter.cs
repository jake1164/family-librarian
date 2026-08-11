using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Infrastructure.Persistence;

namespace FamilyLibrarian.Infrastructure.Integrations;

public sealed class AuditWriter(
    AppDbContext database,
    ICurrentUser currentUser,
    IClock clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    public async Task WriteAsync(
        string action,
        string subjectType,
        string? subjectId,
        object? detail,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AuditEvent(
            action,
            subjectType,
            subjectId,
            currentUser.UserId,
            currentUser.DisplayName,
            detail is null ? null : JsonSerializer.Serialize(detail, SerializerOptions),
            clock.UtcNow);

        database.AuditEvents.Add(auditEvent);
        await database.SaveChangesAsync(cancellationToken);
    }
}
