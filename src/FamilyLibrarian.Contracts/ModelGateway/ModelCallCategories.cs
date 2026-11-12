namespace FamilyLibrarian.Contracts.ModelGateway;

/// <summary>Presentation categories for recorded call outcomes; a negative book assessment can be an accepted reply.</summary>
public static class ModelCallCategories
{
    public const string AcceptedReply = "AcceptedReply";
    public const string RejectedReply = "RejectedReply";
    public const string ProviderError = "ProviderError";
    public const string Cancelled = "Cancelled";
    public const string Pending = "Pending";
    public const string Unknown = "Unknown";

    public static string Classify(string outcome, string? reasonCode) => reasonCode == "Cancelled" ? Cancelled : outcome switch
    {
        "Succeeded" => AcceptedReply,
        "EmptyContent" or "InvalidJson" or "SchemaRejected" or "OutputTooLarge" or "OutputLimitReached" => RejectedReply,
        "TransportFailure" or "Timeout" or "ModelUnavailable" or "UnsupportedCapability" => ProviderError,
        "Pending" => Pending,
        _ => Unknown
    };

    public static bool IsDiagnosticCategory(string category) => category is RejectedReply or ProviderError or Cancelled or Unknown;

    public static string Label(string category) => category switch
    {
        AcceptedReply => "Accepted replies", RejectedReply => "Rejected replies", ProviderError => "Provider errors",
        Cancelled => "Cancelled", Pending => "Pending", _ => "Unclassified"
    };
}
