using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Core.Model;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterBibleLlmInputLoggerTests
{
    [Fact]
    public void DebugInput_WritesFullDynamicModelWithoutShortTextTruncation()
    {
        var paragraphText = "Пончик вошёл. " + new string('я', 600);
        var input = new CharacterExtractionPromptBuilder().BuildPromptInput(
            [("1.1.1.p3", paragraphText)]);
        var logger = new CapturingCharacterBibleRunLogger();

        using (CharacterBibleRunLogScope.Push(logger))
        {
            CharacterBibleLlmInputLogger.DebugInput(
                "extract.llm.input",
                "batchIndex=1 paragraphCount=1",
                input);
        }

        var block = Assert.Single(logger.Blocks);
        Assert.Equal("extract.llm.input", block.EventName);
        Assert.Contains("\"pointer\": \"1.1.1.p3\"", block.Body, StringComparison.Ordinal);
        Assert.Contains(paragraphText, block.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("...", block.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugProfileUpdateContract_ReportsExpectedShape()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var input = new CharacterProfileUpdatePromptInput(
            new CharacterProfileUpdateTarget("Пончик"),
            new CharacterProfileUpdateCurrentProfile(null, null, null, null),
            [new CharacterProfileUpdateEvidence("1.1.1.p3", "Пончик вошёл.")]);

        using (CharacterBibleRunLogScope.Push(logger))
        {
            CharacterBibleLlmInputLogger.DebugProfileUpdateContract(input);
        }

        var diagnostic = Assert.Single(logger.DebugMessages);
        Assert.Equal("profile.update.llm.input.contract", diagnostic.EventName);
        Assert.Contains("topLevelKeys=[\"target\", \"currentProfile\", \"newEvidence\"]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("forbiddenKeysFound=[]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("evidenceCount=1", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("emptyEvidenceTexts=[]", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugInput_PreservesExplicitNullProfileFields()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var input = new CharacterProfileUpdatePromptInput(
            new CharacterProfileUpdateTarget("Пончик"),
            new CharacterProfileUpdateCurrentProfile(null, null, null, null),
            [new CharacterProfileUpdateEvidence("1.1.1.p3", "Пончик вошёл.")]);

        using (CharacterBibleRunLogScope.Push(logger))
        {
            CharacterBibleLlmInputLogger.DebugInput("profile.update.llm.input", "characterId=c1", input);
        }

        var block = Assert.Single(logger.Blocks);
        Assert.Contains("\"appearance\": null", block.Body, StringComparison.Ordinal);
        Assert.Contains("\"psychologicalProfile\": null", block.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterBibleExtractedCandidateDiagnostics_DoNotExposeCandidateId()
    {
        Assert.Null(typeof(CharacterBibleCharacterCandidate).GetProperty("CandidateId"));
        Assert.Null(typeof(SuspectArchiveEntry).GetProperty("CandidateId"));
        Assert.Null(typeof(CharacterEvidenceIndexEntry).GetProperty("CandidateId"));
        Assert.Null(typeof(IdentityConflictRecord).GetProperty("CandidateId"));
    }

    [Fact]
    public async Task ResolveCandidateLogs_UseCandidateIndex()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var applier = new CharacterBibleCandidateResolutionApplier(new CharacterBibleExtractionLimits());
        var session = CharacterDossierEditSession.CreateFrom(new CharacterDossiers(
            "test",
            3,
            [],
            1));
        var candidate = new CharacterBibleCharacterCandidate(
            "Пончик",
            "male",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Пончика"] = "Пончика позвали."
            },
            [new CharacterBibleCandidateEvidence("1.1.1.p3", "Пончика позвали.")]);

        using (CharacterBibleRunLogScope.Push(logger))
        {
            await applier.ResolveAndUpdateCatalogAsync(
                new CharacterBibleWorkflowInput(),
                session,
                1,
                [candidate],
                (_, _, _, _) => Task.FromResult(IdentityResolutionDecision.New("No archive match.")));
        }

        var messages = logger.InfoMessages.Select(message => message.Message).ToArray();
        Assert.Contains(messages, message => message.Contains("candidateIndex=1", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("candidateId=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewDecision_DoesNotContainCharacterIdUntilApplyCreatesCharacter()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var applier = new CharacterBibleCandidateResolutionApplier(new CharacterBibleExtractionLimits());
        var session = CharacterDossierEditSession.CreateFrom(new CharacterDossiers(
            "test",
            3,
            [],
            1));
        var candidate = new CharacterBibleCharacterCandidate(
            "Пончик",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [new CharacterBibleCandidateEvidence("1.1.1.p3", "Пончик вошёл.")]);
        var identityDecision = IdentityResolutionDecision.New("No archive match.");

        Assert.Equal(IdentityResolutionKind.New, identityDecision.Kind);
        Assert.Null(identityDecision.CharacterId);
        Assert.Empty(identityDecision.CharacterIds);

        using (CharacterBibleRunLogScope.Push(logger))
        {
            await applier.ResolveAndUpdateCatalogAsync(
                new CharacterBibleWorkflowInput(),
                session,
                1,
                [candidate],
                (_, _, _, _) => Task.FromResult(identityDecision));
        }

        var created = Assert.Single(session.Current.Characters);
        Assert.Equal(1, created.CharacterId);
        Assert.Equal(2, session.Current.NextCharacterId);
        Assert.Contains(logger.InfoMessages, message =>
            message.EventName == "resolve.decision"
            && message.Message.Contains("decision=new", StringComparison.Ordinal)
            && message.Message.Contains("characterId=null", StringComparison.Ordinal));
        Assert.Contains(logger.InfoMessages, message =>
            message.EventName == "resolve.apply"
            && message.Message.Contains("characterId=1", StringComparison.Ordinal));
        Assert.Contains(logger.InfoMessages, message =>
            message.EventName == "archive.character.created"
            && message.Message.Contains("characterId=1", StringComparison.Ordinal)
            && message.Message.Contains("nextCharacterId=2", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.InfoMessages, message =>
            message.Message.Contains("entryId", StringComparison.OrdinalIgnoreCase)
            || message.Message.Contains("entryIds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resolver_WhenArchiveIsEmpty_CreatesNewCharacterWithoutLlmOrVectorSearch()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var resolver = new CharacterBibleResolver(
            new CharacterBibleExtractionLimits(),
            new ThrowingIdentityResolutionModelClient(),
            new ThrowingCharacterVectorSearchTool());
        var session = CharacterDossierEditSession.CreateFrom(new CharacterDossiers(
            "test",
            3,
            [],
            1));
        var candidate = new CharacterBibleCharacterCandidate(
            "Незнайка",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [new CharacterBibleCandidateEvidence("1.1.1.p1", "Незнайка вошёл.")]);

        using (CharacterBibleRunLogScope.Push(logger))
        {
            await resolver.ResolveAndUpdateCatalogAsync(
                new CharacterBibleWorkflowInput(),
                session,
                1,
                [candidate]);
        }

        var created = Assert.Single(session.Current.Characters);
        Assert.Equal(1, created.CharacterId);
        Assert.Equal("Незнайка", created.Name);
        var decision = Assert.Single(session.Decisions);
        Assert.Equal(CharacterBibleDecisionKind.New, decision.Kind);
        Assert.Equal(1, decision.CharacterId);
        Assert.Contains(logger.InfoMessages, message =>
            message.EventName == "resolve.fast_path.empty_archive"
            && message.Message.Contains("candidateIndex=1", StringComparison.Ordinal)
            && message.Message.Contains("decision=new", StringComparison.Ordinal));
        Assert.Contains(logger.InfoMessages, message => message.EventName == "archive.character.created");
        Assert.Contains(logger.InfoMessages, message =>
            message.EventName == "resolve.apply"
            && message.Message.Contains("decision=new", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Blocks, block => block.EventName == "resolve.llm.input");
    }

    [Fact]
    public async Task ExistingDecision_ContainsCharacterId()
    {
        var applier = new CharacterBibleCandidateResolutionApplier(new CharacterBibleExtractionLimits());
        var session = CharacterDossierEditSession.CreateFrom(new CharacterDossiers(
            "test",
            3,
            [new CharacterDossier(6, "Пончик", [], new Dictionary<string, string>())],
            7));
        var candidate = new CharacterBibleCharacterCandidate(
            "Пончик",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [new CharacterBibleCandidateEvidence("1.1.1.p3", "Пончик вошёл.")]);
        var identityDecision = IdentityResolutionDecision.Existing(6, "Matched by name.");

        Assert.Equal(IdentityResolutionKind.Existing, identityDecision.Kind);
        Assert.Equal(6, identityDecision.CharacterId);

        await applier.ResolveAndUpdateCatalogAsync(
            new CharacterBibleWorkflowInput(),
            session,
            1,
            [candidate],
            (_, _, _, _) => Task.FromResult(identityDecision));

        var decision = Assert.Single(session.Decisions);
        Assert.Equal(CharacterBibleDecisionKind.Existing, decision.Kind);
        Assert.Equal(6, decision.CharacterId);
        Assert.Equal(7, session.Current.NextCharacterId);
    }

    [Fact]
    public async Task DeferDecision_PreservesReadableSuspectDiagnostics()
    {
        var applier = new CharacterBibleCandidateResolutionApplier(new CharacterBibleExtractionLimits());
        var session = CharacterDossierEditSession.CreateFrom(new CharacterDossiers(
            "test",
            3,
            [],
            1));
        var candidate = new CharacterBibleCharacterCandidate(
            "Пончик",
            "male",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Пончика"] = "Пончика позвали."
            },
            [new CharacterBibleCandidateEvidence("1.1.1.p3", "Пончика позвали.")]);

        await applier.ResolveAndUpdateCatalogAsync(
            new CharacterBibleWorkflowInput(),
            session,
            1,
            [candidate],
            (_, _, _, _) => Task.FromResult(IdentityResolutionDecision.Defer([], "Needs more evidence.")));

        var suspect = Assert.Single(session.Current.SuspectArchive!);
        Assert.Equal("Пончик", suspect.CanonicalName);
        Assert.Equal("male", suspect.Gender);
        Assert.Equal(["Пончика"], suspect.Aliases);
        Assert.Equal("Needs more evidence.", suspect.Reason);
        var evidence = Assert.Single(suspect.Evidence);
        Assert.Equal("1.1.1.p3", evidence.Pointer);
        Assert.Equal("Пончика позвали.", evidence.Excerpt);
        Assert.Null(evidence.CharacterId);
    }

    private sealed class CapturingCharacterBibleRunLogger : ICharacterBibleRunLogger
    {
        public CharacterBibleRunLogContext Context { get; } = new(
            "test",
            "test.log",
            DateTimeOffset.UnixEpoch);

        public List<(string EventName, string Header, string Body)> Blocks { get; } = [];

        public List<(string EventName, string Message)> DebugMessages { get; } = [];

        public List<(string EventName, string Message)> InfoMessages { get; } = [];

        public void Info(string eventName, string message)
        {
            InfoMessages.Add((eventName, message));
        }

        public void Debug(string eventName, string message)
        {
            DebugMessages.Add((eventName, message));
        }

        public void DebugBlock(string eventName, string header, string block)
        {
            Blocks.Add((eventName, header, block));
        }

        public void Warning(string eventName, string message)
        {
        }

        public void Error(string eventName, string message, Exception? exception = null)
        {
        }
    }

    private sealed class ThrowingIdentityResolutionModelClient : ICharacterIdentityResolutionModelClient
    {
        public Task<CharacterIdentityResolutionResponse> ResolveAsync(
            CharacterIdentityResolutionModelRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Identity resolver should not be called for an empty archive.");
        }
    }

    private sealed class ThrowingCharacterVectorSearchTool : ICharacterVectorSearchTool
    {
        public Task<IReadOnlyList<CharacterVectorSearchHit>> SearchAsync(
            CharacterDossiers dossiers,
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Vector search should not be called for an empty archive.");
        }
    }
}

