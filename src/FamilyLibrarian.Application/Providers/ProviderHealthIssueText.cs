using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Plain-text rendering of provider-reported <see cref="ProviderHealthIssue"/>s
/// for status lines, the readiness detail and notifications. The messages were
/// bounded and sanitized when parsed; this only joins them. Callers must keep
/// treating the result as untrusted text (never markup).
/// </summary>
public static class ProviderHealthIssueText
{
    /// <summary>"Search: msg Acquire: msg" -- or just the message for a <c>general</c> issue. Null when empty.</summary>
    public static string? Describe(IReadOnlyList<ProviderHealthIssue> issues)
    {
        if (issues.Count == 0)
        {
            return null;
        }

        return string.Join(" ", issues.Select(issue => issue.Operation switch
        {
            ProviderHealthIssue.Operations.Search => $"Search: {issue.Message}",
            ProviderHealthIssue.Operations.Acquire => $"Acquire: {issue.Message}",
            _ => issue.Message
        }));
    }

    /// <summary>A leading-space suffix for appending to a sentence, or empty when there are no issues.</summary>
    public static string AsSuffix(IReadOnlyList<ProviderHealthIssue> issues) =>
        Describe(issues) is { } text ? $" Provider reports: {text}" : string.Empty;
}
