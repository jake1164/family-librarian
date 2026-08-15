using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Security;

public interface IAssetValidator
{
    string Id { get; }

    Task<ValidationOutcome> ValidateAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken);
}

public sealed record ValidationOutcome(bool IsValid, string? Message);
