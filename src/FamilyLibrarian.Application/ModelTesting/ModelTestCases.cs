namespace FamilyLibrarian.Application.ModelTesting;

/// <summary>
/// The fixed prompts of the model capability test. Everything here is invented: made-up titles, authors and evidence. No
/// library data, request, downloaded file or personal information is ever placed in a prompt, and the prompts do not vary
/// with anything a user or a book can supply.
/// </summary>
internal static class ModelTestCases
{
    public const string ToolName = "lookup_catalog_title";
    public const string FactsTitle = "The Lantern Road";
    public const int FactsYear = 2019;

    public static readonly IReadOnlyList<(string Message, string Expected)> IntentCases =
    [
        ("Find Weather by Mara Quill", "FindBook"),
        ("What's the weather tomorrow?", "OutOfDomain"),
        ("Please get the second Lantern Road book as an audiobook", "RequestBook"),
        ("Is my request ready yet?", "CheckRequestStatus")
    ];

    public sealed record MatchScenario(string Name, string Prompt, IReadOnlyList<string> EvidenceIds, IReadOnlyList<string> Acceptable);

    public static readonly IReadOnlyList<MatchScenario> MatchScenarios =
    [
        new("strong agreement",
            "Requested work: \"The Lantern Road\" by Mara Quill (2019, English).\n" +
            "Candidate file facts:\n" +
            "ev-1: embedded title is \"The Lantern Road\"\n" +
            "ev-2: embedded author is \"Mara Quill\"\n" +
            "ev-3: declared language is English\n" +
            "ev-4: publication year is 2019",
            ["ev-1", "ev-2", "ev-3", "ev-4"],
            ["likely_match"]),
        new("conflicting facts",
            "Requested work: \"The Lantern Road\" by Mara Quill (2019, English).\n" +
            "Candidate file facts:\n" +
            "ev-1: embedded title is \"The Lantern Road: A Companion Guide\"\n" +
            "ev-2: embedded author is \"Orrin Vale\"\n" +
            "ev-3: declared language is German\n" +
            "ev-4: publication year is 2007",
            ["ev-1", "ev-2", "ev-3", "ev-4"],
            ["likely_different", "uncertain"])
    ];

    public static ModelRequest ConnectivityRequest(int maxOutputTokens) => new(
        [new("system", "You are a connectivity probe. Reply with exactly one word: pong"), new("user", "ping")],
        Math.Min(maxOutputTokens, 32));

    public static ModelRequest IntentRequest(string message, int maxOutputTokens) => new(
        [
            new("system",
                "You label messages sent to a book library assistant. Reply with only a JSON object of the form " +
                "{\"intent\": \"<label>\"}, where <label> is exactly one of: FindBook (looking for a book), RequestBook " +
                "(asking for a book to be obtained), CheckRequestStatus (asking about an earlier request), OutOfDomain " +
                "(not about books). No other text."),
            new("user", message)
        ],
        maxOutputTokens, JsonObjectOutput: true);

    public static ModelRequest StructuredJsonRequest(int maxOutputTokens) => new(
        [
            new("system",
                "Extract facts from the text. Reply with only a JSON object with exactly these fields: " +
                "\"title\" (string), \"author\" (string), \"year\" (integer). No other fields, no other text."),
            new("user", "\"The Lantern Road\" is a fictional novel written by Mara Quill and first published in 2019.")
        ],
        maxOutputTokens, JsonObjectOutput: true);

    public static ModelRequest MatchAdvisoryRequest(MatchScenario scenario, int maxOutputTokens) => new(
        [
            new("system",
                "You compare a requested book with facts read from a candidate file, using only the numbered facts given. " +
                "Reply with only a JSON object with exactly two fields: \"assessment\" (one of: likely_match, " +
                "likely_different, uncertain) and \"reasons\" (an array of one to three objects, each with exactly " +
                "\"text\" (a short sentence) and \"evidence_id\" (one of the supplied fact ids)). Do not add any other " +
                "field, such as a confidence number. No other text."),
            new("user", scenario.Prompt)
        ],
        maxOutputTokens, JsonObjectOutput: true);

    public static ModelRequest ToolRequest(int maxOutputTokens) => new(
        [
            new("system", "You may call the supplied tool. Never invent a tool."),
            new("user", "Use the tool to look up the fictional title \"The Lantern Road\".")
        ],
        maxOutputTokens,
        Tools: [new ModelToolDefinition(ToolName, "Looks up a fictional catalog title. Test only; never executed.",
            "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"]}")]);

    /// <summary>
    /// Offline: feeds the validators replies a misbehaving model could produce. Returns true only if every one is rejected
    /// safely (no exception) and a correct reply is accepted.
    /// </summary>
    public static bool RunMalformedResponseSelfTest()
    {
        string?[] bad =
        [
            null,
            string.Empty,
            "   ",
            "pong",
            "not json at all",
            "```json\n{\"intent\":\"FindBook\"}\n```",
            "Sure! {\"intent\":\"FindBook\"}",
            "[\"FindBook\"]",
            "{}",
            "{\"intent\":\"FindBook\",\"confidence\":0.99}",
            "{\"intent\":\"DeleteEverything\"}",
            "{\"intent\":7}",
            "{\"intent\":\"FindBook\",\"intent\":\"OutOfDomain\"}",
            "{\"intent\":\"FindBook\"",
            new string('x', ModelOutputValidator.MaxContentLength + 1),
            "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":1}}}}}}}}",
            "{\"intent\":\"Find\u0007Book\"}"
        ];
        try
        {
            if (bad.Any(content => ModelOutputValidator.ValidateIntent(content).IsValid))
            {
                return false;
            }

            string?[] badFacts = ["{\"title\":\"A\",\"author\":\"B\"}", "{\"title\":\"A\",\"author\":\"B\",\"year\":\"2019\"}", "{\"title\":\"A\",\"author\":\"B\",\"year\":1.5}", "{\"title\":\"A\",\"author\":\"B\",\"year\":99999}"];
            if (badFacts.Any(content => ModelOutputValidator.ValidateBookFacts(content).IsValid))
            {
                return false;
            }

            string?[] badAdvisory =
            [
                "{\"assessment\":\"likely_match\",\"reasons\":[]}",
                "{\"assessment\":\"certain\",\"reasons\":[{\"text\":\"x\",\"evidence_id\":\"ev-1\"}]}",
                "{\"assessment\":\"likely_match\",\"reasons\":[{\"text\":\"x\",\"evidence_id\":\"ev-9\"}]}",
                "{\"assessment\":\"likely_match\",\"reasons\":[{\"text\":\"x\",\"evidence_id\":\"ev-1\",\"confidence\":1}]}",
                "{\"assessment\":\"likely_match\",\"confidence\":0.9,\"reasons\":[{\"text\":\"x\",\"evidence_id\":\"ev-1\"}]}"
            ];
            if (badAdvisory.Any(content => ModelOutputValidator.ValidateMatchAdvisory(content, ["ev-1", "ev-2"]).IsValid))
            {
                return false;
            }

            return ModelOutputValidator.ValidateIntent("{\"intent\":\"FindBook\"}").IsValid &&
                   ModelOutputValidator.ValidateBookFacts("{\"title\":\"A\",\"author\":\"B\",\"year\":2019}").IsValid &&
                   ModelOutputValidator.ValidateMatchAdvisory(
                       "{\"assessment\":\"uncertain\",\"reasons\":[{\"text\":\"Only the title is shown.\",\"evidence_id\":\"ev-1\"}]}", ["ev-1"]).IsValid;
        }
        catch (Exception)
        {
            // A validator that throws on hostile input is itself a failure of the check.
            return false;
        }
    }
}
