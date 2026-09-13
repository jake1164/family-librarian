namespace FamilyLibrarian.Infrastructure.Metadata;

public sealed class HardcoverMetadataOptions
{
    public const string SectionName = "MetadataProviders:Hardcover";

    public bool Enabled { get; set; }

    public int MaxResults { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 15;

    // Hardcover's free tier caps daily volume at 5,000 requests; a request
    // that keeps retrying past a handful of attempts burns quota faster than
    // it buys success, so this is deliberately small.
    public int MaxRetryAttempts { get; set; } = 3;

    public int MaxRetryDelaySeconds { get; set; } = 30;
}
