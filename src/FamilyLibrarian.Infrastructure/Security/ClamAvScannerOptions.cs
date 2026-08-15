namespace FamilyLibrarian.Infrastructure.Security;

public sealed class ClamAvScannerOptions
{
    public const string SectionName = "ClamAv";

    public string Host { get; set; } = "clamav";

    public int Port { get; set; } = 3310;

    public int TimeoutSeconds { get; set; } = 30;
}
