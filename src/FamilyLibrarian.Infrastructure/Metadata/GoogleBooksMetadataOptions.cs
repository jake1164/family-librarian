namespace FamilyLibrarian.Infrastructure.Metadata;

public sealed class GoogleBooksMetadataOptions
{
    public const string SectionName = "MetadataProviders:GoogleBooks";

    public bool Enabled { get; set; }

    public string? ApiKey { get; set; }

    public int MaxResults { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 15;
}
