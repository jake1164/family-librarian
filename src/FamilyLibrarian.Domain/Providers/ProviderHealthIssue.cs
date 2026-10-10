namespace FamilyLibrarian.Domain.Providers;

/// <summary>
/// One provider-reported reason behind a degraded or unavailable
/// <c>/health</c> result (protocol v2 §5 <c>issues</c>). Provider-authored
/// and therefore untrusted: the health client bounds and sanitizes it before
/// it ever reaches this type, and every surface renders it as plain text.
/// </summary>
/// <param name="Operation">One of <see cref="Operations"/>.</param>
/// <param name="Code">Optional short, stable, provider-defined identifier.</param>
/// <param name="Message">Plain-text, human-readable explanation.</param>
public sealed record ProviderHealthIssue(string Operation, string? Code, string Message)
{
    public static class Operations
    {
        public const string Search = "search";
        public const string Acquire = "acquire";
        public const string General = "general";
    }

    public static bool IsKnownOperation(string? operation) =>
        operation is Operations.Search or Operations.Acquire or Operations.General;
}
