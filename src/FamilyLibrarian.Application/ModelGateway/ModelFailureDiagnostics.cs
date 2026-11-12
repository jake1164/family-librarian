using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelGateway;

/// <summary>Fixed administrator explanations. Never include arbitrary provider or model text.</summary>
public static class ModelFailureDiagnostics
{
    public static string Explain(ModelCallOutcome outcome, ModelFailureReason? reason) => reason switch
    {
        ModelFailureReason.ProviderUnreachable => "The model provider could not be reached or the client could not complete the request.",
        ModelFailureReason.CredentialsRejected => "The model provider rejected the configured credentials. Check the API key and its permissions.",
        ModelFailureReason.ProviderRateLimited => "The model provider rate-limited the request.",
        ModelFailureReason.ProviderBadRequest => "The model provider rejected the request parameters.",
        ModelFailureReason.ProviderError => "The model provider returned a server error.",
        ModelFailureReason.InvalidProviderResponse => "The model provider returned an unreadable response envelope.",
        ModelFailureReason.Cancelled => "The call was cancelled before a usable response was recorded; cancellation may include a caller time limit.",
        ModelFailureReason.InvalidStructure => "The response did not match the required fields, values or text constraints.",
        ModelFailureReason.NoCitedEvidence => "A claim or proposed action did not cite the evidence it requires.",
        ModelFailureReason.NoSupportingEvidence => "The cited evidence did not support the claim or proposed action.",
        ModelFailureReason.NoContradictingEvidence => "The cited evidence did not provide the required contradiction or concern.",
        ModelFailureReason.UngroundedConcern => "A concern cited only Positive or Unknown evidence. It requires Negative, Weak or Condition evidence; Positive or Unknown findings alone cannot justify a hold.",
        ModelFailureReason.UnknownEvidence => "The response cited evidence that was not supplied or was not allowed for that action.",
        ModelFailureReason.DuplicateEvidence => "The response cited the same evidence more than once.",
        ModelFailureReason.UnknownClaim => "The response used a claim type outside the allowed contract.",
        ModelFailureReason.UngroundedVerdict => "The proposed verdict lacked the required grounded claims.",
        ModelFailureReason.RepeatedSearch => "The response suggested a search already tried, the original request, or a duplicate suggestion.",
        ModelFailureReason.InvalidSearch => "The proposed search did not meet the allowed bibliographic text constraints.",
        ModelFailureReason.DisallowedAction => "The proposed action was not permitted by the current candidate or file rules.",
        ModelFailureReason.ExcerptCopied => "The response reproduced book text instead of a permitted short assessment.",
        ModelFailureReason.MissingDerivedFact => "An excerpt citation was missing its required derived evidence fact.",
        ModelFailureReason.ParserError => "The response could not be interpreted; the parser encountered an error.",
        _ => outcome switch
        {
            ModelCallOutcome.TransportFailure => "The provider could not complete the request (connection, credentials, rate limit or server error). No finer reason was recorded.",
            ModelCallOutcome.Timeout => "The model did not answer within the allowed time.",
            ModelCallOutcome.EmptyContent => "The model returned no text.",
            ModelCallOutcome.InvalidJson => "The response was not valid JSON in the required format.",
            ModelCallOutcome.SchemaRejected => "The response failed schema or evidence validation. A detailed diagnostic code was not recorded for this call.",
            ModelCallOutcome.UnsupportedCapability => "The configured model or provider did not support a requested capability.",
            ModelCallOutcome.ModelUnavailable => "The configured model was unavailable or had no healthy deployment.",
            ModelCallOutcome.OutputTooLarge => "The response exceeded the allowed output size.",
            ModelCallOutcome.OutputLimitReached => "The model stopped at the output token limit before completing its answer.",
            _ => "No additional diagnostic reason was recorded."
        }
    };
}
