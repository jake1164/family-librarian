using System.Globalization;
using FamilyLibrarian.Contracts.ModelGateway;

namespace FamilyLibrarian.Web.Client.ModelGateway;

/// <summary>Operational signals only. Accepted output is not evidence of accuracy or downstream benefit.</summary>
public sealed record AiQualityMetrics(int Calls, int Pending, int Replies, int Usable, int ProviderErrors, int Cancelled, int Unclassified,
    string? ProviderIssue, string? RejectionIssue)
{
    public int UnusableReplies => Replies - Usable;
    public string ProviderRate => Rate(Replies, Replies + ProviderErrors);
    public string UsableRate => Rate(Usable, Replies);
    public string Signal => Calls == 0 ? "Not used"
        : Replies + ProviderErrors == 0 ? "No evaluated responses"
        : ProviderErrors > 0 && UnusableReplies > 0 ? "Provider errors and rejected replies"
        : ProviderErrors > 0 ? "Provider errors"
        : UnusableReplies > 0 ? "Replies rejected"
        : "Replies accepted";

    public static AiQualityMetrics From(IEnumerable<ModelUsageRowResponse> source)
    {
        var rows = source.ToArray();
        int Count(Func<ModelUsageRowResponse, bool> filter) => rows.Where(filter).Sum(row => row.Calls);
        string Category(ModelUsageRowResponse row) => ModelCallCategories.Classify(row.Outcome, row.ReasonCode);
        var accepted = Count(row => Category(row) == ModelCallCategories.AcceptedReply);
        var replies = accepted + Count(row => Category(row) == ModelCallCategories.RejectedReply);
        var providerErrors = Count(row => Category(row) == ModelCallCategories.ProviderError);
        var cancelled = Count(row => Category(row) == ModelCallCategories.Cancelled);
        var pending = Count(row => Category(row) == ModelCallCategories.Pending);
        var calls = rows.Sum(row => row.Calls);
        string? CommonIssue(string category)
        {
            var issue = rows.Where(row => Category(row) == category).GroupBy(row => Issue(row))
                .Select(group => new { Label = group.Key, Count = group.Sum(row => row.Calls) })
                .OrderByDescending(group => group.Count).ThenBy(group => group.Label, StringComparer.Ordinal).FirstOrDefault();
            return issue is null ? null : $"{issue.Label}: {issue.Count:N0}";
        }
        return new(calls, pending, replies, accepted, providerErrors, cancelled,
            calls - pending - replies - providerErrors - cancelled,
            CommonIssue(ModelCallCategories.ProviderError), CommonIssue(ModelCallCategories.RejectedReply));
    }

    public static string Rate(int numerator, int denominator)
    {
        if (denominator == 0) return "No data";
        if (numerator == 0) return $"0% (0/{denominator:N0})";
        if (numerator == denominator) return $"100% ({numerator:N0}/{denominator:N0})";
        var percent = 100m * numerator / denominator;
        var label = percent > 99.9m ? ">99.9" : percent < 0.1m ? "<0.1" : percent.ToString("0.#", CultureInfo.CurrentCulture);
        return $"{label}% ({numerator:N0}/{denominator:N0})";
    }

    private static string Issue(ModelUsageRowResponse row) => row.ReasonCode switch
    {
        "UngroundedConcern" => "Unsupported concerns",
        "NoSupportingEvidence" => "Claims lack supporting evidence",
        "NoContradictingEvidence" => "Claims lack contradicting evidence",
        "RepeatedSearch" => "Repeated search suggestions",
        "UnknownEvidence" => "Invalid evidence references",
        "Cancelled" => "Cancelled calls",
        "CredentialsRejected" => "Credentials rejected",
        "ProviderRateLimited" => "Provider rate limits",
        _ => row.Outcome switch
        {
            "SchemaRejected" => "Schema/evidence rejections", "InvalidJson" => "Invalid JSON",
            "EmptyContent" => "Empty responses", "OutputLimitReached" => "Output token limits",
            "OutputTooLarge" => "Oversized responses", "Timeout" => "Timeouts",
            "TransportFailure" => "Provider request errors", "ModelUnavailable" => "Unavailable model",
            "UnsupportedCapability" => "Unsupported capability", _ => "Unclassified outcomes"
        }
    };

    public static string Recommendation(string verdict) => verdict switch
    {
        "Consistent" => "Evidence agrees", "Inconsistent" => "Evidence conflicts", "Uncertain" => "Uncertain",
        "Search" => "Searches proposed", "GiveUp" => "No further searches proposed",
        "keep" => "Keep current choice", "veto" => "Veto proposed", "prefer" => "Tie preference proposed",
        "author_variant" => "Author variant proposed", "hold" => "Hold proposed", "no_objection" => "No objection",
        "release" => "Release proposed", "contains_requested" => "Collection match proposed", _ => "Other validated result"
    };
}
