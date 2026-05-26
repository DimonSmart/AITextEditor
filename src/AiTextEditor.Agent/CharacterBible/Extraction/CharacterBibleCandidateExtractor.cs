using System.Text.Json;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal sealed class CharacterBibleCandidateExtractor
{
    private const string CharacterExtractionInvalidContractError = "character_extraction_response_contract_invalid";

    private readonly ICharacterExtractionModelClient characterExtractionModelClient;
    private readonly CharacterExtractionPromptBuilder promptBuilder;
    private readonly CharacterBibleParagraphBatcher paragraphBatcher;
    private readonly CandidatePostProcessor postProcessor;
    private readonly ILogger<CharacterBibleCandidateExtractor> logger;

    public CharacterBibleCandidateExtractor(
        ICharacterExtractionModelClient characterExtractionModelClient,
        CharacterExtractionPromptBuilder promptBuilder,
        CharacterBibleParagraphBatcher paragraphBatcher,
        ILogger<CharacterBibleCandidateExtractor> logger)
    {
        this.characterExtractionModelClient = characterExtractionModelClient ?? throw new ArgumentNullException(nameof(characterExtractionModelClient));
        this.promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        this.paragraphBatcher = paragraphBatcher ?? throw new ArgumentNullException(nameof(paragraphBatcher));
        postProcessor = new CandidatePostProcessor();
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterBibleCandidateExtractionResult> ExtractCandidatesAsync(
        IReadOnlyList<TextFragment> paragraphs,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        cancellationToken.ThrowIfCancellationRequested();

        if (paragraphs.Count == 0)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                "No paragraphs available for candidate extraction."));
            return new CharacterBibleCandidateExtractionResult([], CharacterBibleModelResponseErrorStatistics.Empty);
        }

        var candidates = new List<CharacterBibleCharacterCandidate>();
        var seenCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        var modelResponseErrors = new ModelResponseErrorStatisticsBuilder();
        var batchNumber = 0;
        foreach (var batch in paragraphBatcher.SplitParagraphs(paragraphs.Select(p => (p.Pointer, p.Text)).ToList()))
        {
            batchNumber++;
            progress?.Report(new CharacterBibleWorkflowProgress(
                    "extract",
                    $"Extracting candidates from batch {batchNumber} ({batch.Count} paragraphs, {batch[0].Pointer}..{batch[^1].Pointer})."));
            try
            {
                var hits = await ExtractCharactersWithModelAsync(
                    batch,
                    new CharacterBibleModelDiagnosticProgress(progress, modelResponseErrors, batchNumber),
                    cancellationToken);
                var batchCandidates = postProcessor.Process(hits).ToList();
                candidates.AddRange(batchCandidates.Where(candidate => seenCandidateIds.Add(candidate.CandidateId)));
                var batchNames = batchCandidates.Count > 0
                    ? ": " + string.Join(", ", batchCandidates.Select(c => c.CanonicalName))
                    : string.Empty;
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "extract",
                    $"Batch {batchNumber} produced {batchCandidates.Count} character candidates{batchNames}."));
            }
            catch (Exception ex) when (IsRecoverableBatchExtractionError(ex))
            {
                modelResponseErrors.AddSkippedBatch(batch.Count);
                logger.LogError(
                    ex,
                    "Character extraction failed for batch {BatchNumber} ({ParagraphCount} paragraphs, {FirstPointer}..{LastPointer}). Skipping batch.",
                    batchNumber,
                    batch.Count,
                    batch[0].Pointer,
                    batch[^1].Pointer);
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "extract",
                    $"Batch {batchNumber} failed and was skipped ({batch[0].Pointer}..{batch[^1].Pointer}).",
                    IsError: true));
            }
        }

        var statistics = modelResponseErrors.ToStatistics();
        if (statistics.SkippedBatchCount > 0)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Candidate extraction finished with warnings: {candidates.Count} candidates. Skipped {statistics.SkippedBatchCount} failed batches / {statistics.SkippedParagraphCount} paragraphs."));
        }
        else
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Candidate extraction finished: {candidates.Count} candidates."));
        }

        return new CharacterBibleCandidateExtractionResult(candidates, statistics);
    }

    private async Task<List<CharacterExtractionCharacter>> ExtractCharactersWithModelAsync(
        IReadOnlyList<(string Pointer, string Text)> paragraphs,
        IProgress<AgenticModelDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (paragraphs.Count == 0)
        {
            return [];
        }

        var extractionResponse = await characterExtractionModelClient.ExtractCharactersAsync(
            new CharacterExtractionModelRequest(
                promptBuilder.BuildSystemPrompt(),
                promptBuilder.BuildUserPrompt(paragraphs),
                diagnostics),
            cancellationToken);

        return extractionResponse.Characters;
    }

    private static bool IsRecoverableBatchExtractionError(Exception ex)
    {
        return ex is JsonException
            || (ex is InvalidOperationException invalidOperationException
                && string.Equals(invalidOperationException.Message, CharacterExtractionInvalidContractError, StringComparison.Ordinal));
    }

    private sealed class CharacterBibleModelDiagnosticProgress(
        IProgress<CharacterBibleWorkflowProgress>? progress,
        ModelResponseErrorStatisticsBuilder statistics,
        int batchNumber) : IProgress<AgenticModelDiagnostic>
    {
        public void Report(AgenticModelDiagnostic value)
        {
            ArgumentNullException.ThrowIfNull(value);

            statistics.Add(value);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Batch {batchNumber}: {value.Message}",
                value.RawResponse,
                value.RawResponse is null ? null : "Copy raw response",
                AlwaysVisible: true,
                IsError: value.Kind is AgenticModelDiagnosticKind.MalformedResponse or AgenticModelDiagnosticKind.InvalidContract));
        }
    }

    private sealed class ModelResponseErrorStatisticsBuilder
    {
        private int parseErrorCount;
        private int contractErrorCount;
        private int retryCount;
        private int retrySucceededCount;
        private int skippedBatchCount;
        private int skippedParagraphCount;

        public void Add(AgenticModelDiagnostic diagnostic)
        {
            switch (diagnostic.Kind)
            {
                case AgenticModelDiagnosticKind.MalformedResponse:
                    parseErrorCount++;
                    break;
                case AgenticModelDiagnosticKind.InvalidContract:
                    contractErrorCount++;
                    break;
                case AgenticModelDiagnosticKind.Retry:
                    retryCount++;
                    break;
                case AgenticModelDiagnosticKind.RetrySucceeded:
                    retrySucceededCount++;
                    break;
            }
        }

        public void AddSkippedBatch(int paragraphCount)
        {
            skippedBatchCount++;
            skippedParagraphCount += paragraphCount;
        }

        public CharacterBibleModelResponseErrorStatistics ToStatistics()
        {
            return new CharacterBibleModelResponseErrorStatistics(
                parseErrorCount,
                contractErrorCount,
                retryCount,
                retrySucceededCount,
                skippedBatchCount,
                skippedParagraphCount);
        }
    }
}
