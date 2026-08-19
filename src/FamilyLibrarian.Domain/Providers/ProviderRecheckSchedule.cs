namespace FamilyLibrarian.Domain.Providers;

/// <summary>
/// How often an administrator-approved external provider may search again for
/// an otherwise pending request. Manual is the safe default for a newly
/// registered provider.
/// </summary>
public enum ProviderRecheckSchedule
{
    Manual,
    Daily,
    Weekly
}
