namespace FamilyLibrarian.Web.Client.ModelGateway;

public static class ModelRoleLabels
{
    public static string Title(string role) => role switch
    {
        "IntentInterpretation" => "Understand book requests",
        "ConversationResponse" => "Phrase conversational replies",
        "CandidateAmbiguityAssessment" => "Assess ambiguous candidates",
        "ArtifactValidationAssessment" => "Assess downloaded files",
        "VisionCoverIdentification" => "Identify books from cover images",
        "SearchGuidance" => "Suggest additional searches",
        "CandidateTriage" => "Review candidates before acquisition",
        "ArtifactValidationDecision" => "Review files before approval",
        _ => role
    };
}
