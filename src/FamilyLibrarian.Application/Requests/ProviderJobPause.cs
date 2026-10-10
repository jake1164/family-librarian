namespace FamilyLibrarian.Application.Requests;

/// <summary>Generic provider phase convention; unrelated to human interaction waits.</summary>
public static class ProviderJobPause
{
    public static bool IsPaused(string? phase) =>
        string.Equals(phase, "paused", StringComparison.OrdinalIgnoreCase) ||
        phase?.StartsWith("paused-", StringComparison.OrdinalIgnoreCase) == true;
}
