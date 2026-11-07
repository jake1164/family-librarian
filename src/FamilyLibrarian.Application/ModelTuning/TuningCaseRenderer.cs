using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.ModelTuning;

public sealed record ParsedTuningAnswer(bool IsValid, TuningAnswer? Answer, IReadOnlyList<string> EvidenceIds,
    bool AcceptedSafetyViolation = false);
public sealed record RenderedTuningCase(IReadOnlyList<ModelChatMessage> Messages, int MaxOutputTokens,
    IReadOnlyList<EvidenceFinding> Findings, Func<string?, ParsedTuningAnswer> Parse)
{
    public IReadOnlyDictionary<string, string> CandidateIds { get; init; } = new Dictionary<string, string>();
    public string PromptIdentity => RenderedMessagesIdentity.Compute(Messages, true, MaxOutputTokens);
}

/// <summary>Rebuilds inert inputs in memory; never reads a file or contacts a source. Prompts/parsers belong to production.</summary>
public static class TuningCaseRenderer
{
    private static readonly Guid SyntheticId = new("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset SyntheticTime = DateTimeOffset.UnixEpoch;

    public static RenderedTuningCase Render(TuningCase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.SchemaVersion != 1) throw new ArgumentException("Unsupported case schema.", nameof(item));
        return Render(item.Role, item.Input, item.Source, item.Coverage, item.SyntheticSupplement);
    }

    /// <summary>Shared input-to-prompt path; no corpus, labels or review metadata are required.</summary>
    public static RenderedTuningCase Render(FamilyLibrarian.Domain.ModelGateway.ModelRole role, ITuningInput roleInput,
        TuningSource source, TuningCoverage coverage = TuningCoverage.ProductionReachable, SyntheticSupplement? syntheticSupplement = null)
    {
        if (!TuningRoles.IsScored(role) || !Enum.IsDefined(source) || !Enum.IsDefined(coverage))
            throw new ArgumentException("Unsupported role or case source.");
        _ = TuningInputJson.Write(role, roleInput);
        if (syntheticSupplement is { } supplement &&
            (source != TuningSource.BuiltIn || roleInput is not ArtifactHoldInput || supplement.SchemaVersion != 1))
            throw new ArgumentException("Synthetic supplements are built-in hold fixtures only.", nameof(syntheticSupplement));
        return roleInput switch
        {
            ArtifactAssessmentInput assessment => Assessment(assessment, coverage),
            ArtifactHoldInput hold => Hold(hold, syntheticSupplement, coverage),
            CandidateAssessmentInput candidate => Candidate(candidate, coverage),
            CandidateTriageCaseInput triage => Triage(triage),
            SearchCaseInput search => Search(search),
            IntentCaseInput intent => Intent(intent),
            _ => throw new ArgumentException("Unknown role input.", nameof(roleInput))
        };
    }

    private static RenderedTuningCase Prepared(string system, string user, int cap, IReadOnlyList<EvidenceFinding> facts,
        Func<string?, ParsedTuningAnswer> parse) => new([new("system", system), new("user", user)], cap, facts, parse);

    private static RenderedTuningCase Assessment(ArtifactAssessmentInput input, TuningCoverage coverage)
    {
        var asset = Asset(input.Asset);
        var rows = Rows(asset, input.Recorded);
        RequireEligible(ArtifactSemanticEvidence.IsEligible(rows), coverage);
        var findings = ArtifactSemanticEvidence.ToFindings(rows, input.IncludeDetails);
        var work = new WorkSummary(SyntheticId, input.Declared.Title, input.Declared.PrimaryAuthor, [], Language: input.Declared.Language);
        return Prepared(ArtifactSemanticEvidence.SystemPrompt, ArtifactSemanticEvidence.Render(asset, work, findings, input.IncludeDetails),
            ArtifactSemanticAnalyzer.MaxOutputTokens, findings, text => Semantic(SemanticAssessmentParser.Parse(text, SemanticSchema.Artifact, findings)));
    }

    private static RenderedTuningCase Candidate(CandidateAssessmentInput input, TuningCoverage coverage)
    {
        var assessment = Assess(input.Requested, input.Candidate);
        RequireEligible(CandidateSemanticEvidence.IsEligible(assessment), coverage);
        var findings = CandidateSemanticEvidence.ShownFindings(assessment);
        return Prepared(CandidateSemanticEvidence.SystemPrompt, CandidateSemanticEvidence.Render(assessment), CandidateSemanticAnalyzer.MaxOutputTokens,
            findings, text => Semantic(SemanticAssessmentParser.Parse(text, SemanticSchema.Candidate, findings)));
    }

    private static RenderedTuningCase Hold(ArtifactHoldInput input, SyntheticSupplement? supplement, TuningCoverage coverage)
    {
        var asset = Asset(input.Asset);
        var facts = input.Verification.Facts.Select(f => new IdentityFact(f.FindingId, f.Dimension, f.Code, f.Polarity, f.Value)).ToArray();
        var p = input.ExpectedProfile;
        var expected = new ExpectedArtifactProfile(p.Title, p.AlternateTitles, p.PrimaryAuthor, p.Series, p.Isbn13s,
            p.AcceptedLanguage, p.CatalogPageCount, p.CatalogAudioSeconds, p.AcceptedAsCollectionContaining,
            p.PeerMedianSizeBytes, p.PeerCount, p.AssetSizeBytes, p.AcceptedVariantKind);
        var rows = Rows(asset, input.Recorded);
        IReadOnlyList<ArtifactExcerpt> excerpts = input.IncludeDetails ? supplement?.Excerpts ?? [] : [];
        var rendered = ArtifactDecisionEvidence.Render(asset, facts, expected, rows, input.IncludeDetails, excerpts: excerpts);
        foreach (var fact in input.DerivedExcerptFacts)
            if (!rendered.Findings.Any(f => f.Id == fact.FindingId && f.Code == fact.Code && f.Polarity == fact.Polarity))
                throw new ArgumentException("Derived excerpt fact cannot be reproduced by this fixture.", nameof(input));
        RequireEligible(input.Verification.IsMatch && asset.Origin is AssetOrigin.ExternalProvider or AssetOrigin.BuiltInProvider &&
            asset.Format.TrimStart('.').ToUpperInvariant() is "EPUB" or "KEPUB" or "MP3" or "M4B" or "OGG" &&
            !facts.Any(f => f.Dimension is EvidenceDimension.Security or EvidenceDimension.Structure && f.Polarity == EvidencePolarity.Negative) &&
            !rows.Any(f => f.Dimension is EvidenceDimension.Security or EvidenceDimension.Structure && f.Polarity == EvidencePolarity.Negative) &&
            ArtifactDecisionEvidence.CanSupportHold(rendered.Findings), coverage);
        return Prepared(ArtifactDecisionEvidence.SystemPrompt + (excerpts.Count > 0 ? ArtifactDecisionEvidence.ExcerptInstructions : ""),
            rendered.Text, ArtifactDecisionService.MaxOutputTokens, rendered.Findings, text =>
            {
                var parsed = SemanticAssessmentParser.ParseArtifactDecision(text, rendered.Findings, excerpts);
                return parsed.IsValid ? new(true, new(Verdict: parsed.Value!.Verdict),
                    parsed.Value.Claims.SelectMany(c => c.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray()) : Invalid();
            });
    }

    private static RenderedTuningCase Triage(CandidateTriageCaseInput input)
    {
        var options = input.Candidates.Select(c => Option(input.Requested, c)).ToArray();
        if (options.Select(o => o.ProviderResultId).Distinct(StringComparer.Ordinal).Count() != options.Length ||
            input.PickKey is not null && !options.Any(o => o.ProviderResultId == input.PickKey) ||
            input.TiedKeys.Any(key => !options.Any(o => o.ProviderResultId == key))) throw new ArgumentException("Invalid candidate keys.", nameof(input));
        var context = new CandidateTriageContext(input.Requested.Build(), options,
            options.SingleOrDefault(o => o.ProviderResultId == input.PickKey), input.TiedKeys, input.Confidence);
        var rendered = CandidateTriageEvidence.Render(context);
        return Prepared(CandidateTriageEvidence.SystemPrompt, rendered.Text, CandidateTriageService.MaxOutputTokens,
            rendered.Findings.Values.SelectMany(f => f).ToArray(), text =>
            {
                var parsed = SemanticAssessmentParser.ParseCandidateTriage(text, rendered.Findings);
                if (!parsed.IsValid || !CandidateTriageRules.Allows(parsed.Value!, context, rendered)) return Invalid();
                var move = parsed.Value!;
                var target = move.Candidate is null ? null : rendered.Candidates[move.Candidate].ProviderResultId;
                return new(true, new(Move: move.Move, TargetKey: target), move.EvidenceIds,
                    move.Move == "prefer" && target == input.PickKey);
            }) with { CandidateIds = rendered.Candidates.ToDictionary(p => p.Value.ProviderResultId, p => p.Key, StringComparer.Ordinal) };
    }

    private static RenderedTuningCase Search(SearchCaseInput input)
    {
        var requested = input.Requested.Build();
        var options = input.Results.Select(c => Option(input.Requested, c)).ToArray();
        var rendered = SearchGuidanceEvidence.Render(requested, options, input.AlreadyTried);
        var results = options.Select((o, i) => new EvidenceFinding("r" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "search.result", EvidencePolarity.Unknown, o.Title ?? o.ReleaseName ?? string.Empty)).ToArray();
        return Prepared(SearchGuidanceEvidence.SystemPrompt, rendered.Text, SearchGuidanceService.MaxOutputTokens, results, text =>
        {
            var parsed = SemanticAssessmentParser.ParseSearchGuidance(text, requested, rendered.EvidenceIds, input.AlreadyTried);
            if (!parsed.IsValid || SearchGuidanceVariants.Build(requested, parsed.Value!) is null) return Invalid();
            var value = parsed.Value!;
            return new(true, new(GiveUp: value.GiveUp, Suggestions: value.Suggestions.Select(s => new TuningSearchName(s.Reason, s.Value)).ToArray()),
                value.Suggestions.SelectMany(s => s.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray());
        });
    }

    private static RenderedTuningCase Intent(IntentCaseInput input)
    {
        var user = ModelBookIntentInterpreter.RenderUserContent(input.Message, input.PreviousSearch)
            ?? throw new ArgumentException("Empty intent message is not a model case.", nameof(input));
        return Prepared(ModelBookIntentInterpreter.SystemPrompt, user, ModelBookIntentInterpreter.MaxOutputTokens, [], text =>
        {
            var parsed = BookIntentParser.Parse(text);
            if (!parsed.IsValid) return Invalid();
            var value = parsed.Value!;
            var intent = value.Intent switch { BookIntent.FindBook => "find_book", BookIntent.CheckStatus => "check_status",
                BookIntent.OutOfDomain => "out_of_domain", _ => "unclear" };
            return new(true, new(Intent: intent, Title: value.Title, Author: value.Author), []);
        });
    }

    private static ParsedTuningAnswer Semantic(ModelParseResult<SemanticAssessment.SemanticAssessment> parsed) => parsed.IsValid
        ? new(true, new(Verdict: parsed.Value!.Verdict.ToString().ToLowerInvariant()),
            parsed.Value.Claims.SelectMany(c => c.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray()) : Invalid();
    private static ParsedTuningAnswer Invalid() => new(false, null, []);
    private static void RequireEligible(bool eligible, TuningCoverage coverage)
    {
        if (!eligible && coverage == TuningCoverage.ProductionReachable) throw new ArgumentException("Case is not reachable by the production role.");
    }

    private static MediaAsset Asset(TuningAsset input)
    {
        var asset = new MediaAsset(SyntheticId, null, input.MediaType, input.Format, "fixture", "fixture", 4096,
            new string('0', 64), "application/octet-stream", SyntheticId, null, SyntheticTime, origin: input.Origin,
            originUserId: input.Origin == AssetOrigin.ManualUpload ? SyntheticId : null);
        if (input.StorageState != MediaAssetStorageState.Quarantine)
        {
            asset.TransitionStorageState(MediaAssetStorageState.Processing, SyntheticTime);
            if (input.StorageState == MediaAssetStorageState.Archived) asset.TransitionStorageState(MediaAssetStorageState.Trusted, SyntheticTime);
            if (asset.StorageState != input.StorageState) asset.TransitionStorageState(input.StorageState, SyntheticTime);
        }
        return asset;
    }
    private static ArtifactEvidence[] Rows(MediaAsset asset, IReadOnlyList<TuningEvidence> rows) => rows.Select(r =>
        new ArtifactEvidence(asset.Id, asset.Sha256, r.RecordKind, SyntheticId, r.FindingId, r.Dimension, r.Code, r.Polarity,
            "fixture", r.Value, null, SyntheticTime)).ToArray();

    private static CandidateIdentityAssessment Assess(TuningIdentity requested, TuningCandidate candidate)
    {
        var r = candidate.ReleaseFacts;
        var e = candidate.Edition;
        var source = new ExternalProviderCandidate(candidate.Key,
            new(candidate.Work.Title, null, candidate.Work.Authors, candidate.Work.Series, []),
            e is null ? null : new(e.Language, e.PublicationYear, e.Publisher, e.Identifiers),
            r is null ? null : new(r.Name, r.Format, r.SizeBytes, r.IsCollection, null, r.IsSample, r.IsAbridged, r.IsUnabridged, [], null));
        var assessment = DeterministicCandidateIdentityResolver.Assess(requested.Build(), source);
        if (r?.AssertedLanguage is { } language && language != assessment.ReleaseEvidence.AssertedLanguage)
            throw new ArgumentException("Release language is not reproduced by the real resolver.", nameof(candidate));
        return assessment;
    }

    private static FulfillmentOption Option(TuningIdentity requested, TuningOption input)
    {
        var assessment = Assess(requested, input.Candidate);
        var f = input.Fulfillment;
        return new("example-source", input.Candidate.Key, SyntheticId, null, f.MediaType, OptionKind.DirectAcquisition,
            AcquisitionMethod.DirectDownload, f.Format, f.Language, f.IsRetail ? "retail" : null, null, null, null, null, null, null, null,
            MatchBasis: f.MatchBasis, RequiresLanguageConfirmation: f.RequiresLanguageConfirmation, Title: f.Title, Author: f.Author,
            RequiresReleaseConfirmation: f.RequiresReleaseConfirmation, ReleaseConcern: f.ReleaseConcernKind switch
            { ReleaseConcernKind.DrmUnconfirmed => ExternalReleasePolicy.UnknownDrmConfirmationReason, ReleaseConcernKind.Other => "review", _ => null },
            PublicationYear: f.PublicationYear, Publisher: f.Publisher, SizeBytes: f.SizeBytes,
            NarrationKind: f.NarrationKind, RequiresNarrationConfirmation: f.RequiresNarrationConfirmation,
            ReleaseName: input.Candidate.ReleaseFacts?.Name,
            AuthorAffinity: f.AuthorAffinity is { } affinity ? assessment.AuthorEvidence with { Kind = affinity.Kind, Score = affinity.Score } : assessment.AuthorEvidence,
            HasPlausibleTitle: f.HasPlausibleTitle, AudiobookPart: f.AudiobookPart is { } part ? new(part.Number, part.Total) : null,
            FragmentOnlyConcern: f.FragmentOnlyConcern, IdentityAssessment: assessment,
            AcquisitionAssessment: f.AcquisitionSuitability is { } suitability ? new(suitability, []) : null);
    }
}
