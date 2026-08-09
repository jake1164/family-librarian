namespace FamilyLibrarian.Infrastructure.Metadata;

public sealed class OpenLibraryMetadataOptions
{
    public const string SectionName = "MetadataProviders:OpenLibrary";

    public bool Enabled { get; set; }

    public int MaxResults { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 15;

    public int RequestsPerSecond { get; set; } = 1;

    public string? ContactEmail { get; set; }
}
