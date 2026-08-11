namespace FamilyLibrarian.Application.Integrations;

/// <summary>
/// The set of metadata providers compiled into this build.
/// </summary>
/// <remarks>
/// Admin routes address only these known ids. Nothing in the product accepts an
/// arbitrary provider id, executable code, or a caller-supplied target URL.
/// </remarks>
public interface IMetadataProviderRegistry
{
    IReadOnlyList<MetadataProviderDescriptor> GetInstalledProviders();

    MetadataProviderDescriptor? Find(string providerId);
}
