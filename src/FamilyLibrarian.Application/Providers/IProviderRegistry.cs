namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// The set of providers compiled into this build.
/// </summary>
/// <remarks>
/// Admin routes address only these known ids. Nothing in the product accepts an
/// arbitrary provider id, executable code, or a caller-supplied target URL.
/// </remarks>
public interface IProviderRegistry
{
    IReadOnlyList<ProviderDescriptor> GetInstalledProviders();

    ProviderDescriptor? Find(string providerId);
}
