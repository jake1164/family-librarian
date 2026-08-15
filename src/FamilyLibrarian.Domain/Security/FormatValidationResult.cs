namespace FamilyLibrarian.Domain.Security;

/// <summary>One format validator's result against one <see cref="SecurityEvaluation"/>.</summary>
/// <remarks>
/// Kept distinct from <see cref="SecurityScanResult"/>: malware detection and
/// format validity are different findings, produced by different tools, and a
/// reviewer needs to tell them apart.
/// </remarks>
public sealed class FormatValidationResult
{
    private FormatValidationResult()
    {
    }

    internal FormatValidationResult(
        Guid securityEvaluationId,
        string validatorId,
        bool isValid,
        string? message,
        DateTimeOffset validatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(validatorId))
        {
            throw new ArgumentException("A validator id is required.", nameof(validatorId));
        }

        Id = Guid.NewGuid();
        SecurityEvaluationId = securityEvaluationId;
        ValidatorId = validatorId.Trim();
        IsValid = isValid;
        Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        ValidatedAtUtc = validatedAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid SecurityEvaluationId { get; private set; }

    public string ValidatorId { get; private set; } = null!;

    public bool IsValid { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset ValidatedAtUtc { get; private set; }
}
