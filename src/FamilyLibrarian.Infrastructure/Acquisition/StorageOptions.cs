namespace FamilyLibrarian.Infrastructure.Acquisition;

/// <summary>The filesystem root under which the quarantine/processing/rejected/trusted zones live.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = "/data/family-librarian";
}
