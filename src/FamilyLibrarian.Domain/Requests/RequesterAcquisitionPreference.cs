namespace FamilyLibrarian.Domain.Requests;

public enum RequesterAcquisitionPreferenceKind { AcceptCollectionsContaining = 1, AcceptLanguage = 2 }

public sealed class RequesterAcquisitionPreference
{
    private RequesterAcquisitionPreference() { }

    public RequesterAcquisitionPreference(Guid userId, RequesterAcquisitionPreferenceKind kind, string? value, DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A requester is required.", nameof(userId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        UserId = userId;
        Kind = kind;
        Value = kind == RequesterAcquisitionPreferenceKind.AcceptCollectionsContaining ? string.Empty : value?.Trim().ToLowerInvariant();
        if (kind == RequesterAcquisitionPreferenceKind.AcceptLanguage &&
            (string.IsNullOrWhiteSpace(Value) || Value.Length > 16 || Value.Any(c => c is not (>= 'a' and <= 'z') && c != '-')))
            throw new ArgumentException("Choose a language code.", nameof(value));
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public RequesterAcquisitionPreferenceKind Kind { get; private set; }
    public string? Value { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
