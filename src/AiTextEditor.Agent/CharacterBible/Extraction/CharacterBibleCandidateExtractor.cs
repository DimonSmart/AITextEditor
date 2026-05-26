using System.Text.Json;
using AiTextEditor.Agent.CharacterBible;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal sealed class CharacterBibleCandidateExtractor
{
    private const string CharacterExtractionInvalidContractError = "character_extraction_response_contract_invalid";

    private readonly ICharacterExtractionModelClient characterExtractionModelClient;
    private readonly CharacterExtractionPromptBuilder promptBuilder;
    private readonly CharacterBibleParagraphBatcher paragraphBatcher;
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
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<CharacterBibleCharacterCandidate>> ExtractCandidatesAsync(
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
            return [];
        }

        var candidates = new List<CharacterBibleCharacterCandidate>();
        var batchNumber = 0;
        var failedBatchCount = 0;
        var failedParagraphCount = 0;
        foreach (var batch in paragraphBatcher.SplitParagraphs(paragraphs.Select(p => (p.Pointer, p.Text)).ToList()))
        {
            batchNumber++;
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Extracting candidates from batch {batchNumber} ({batch.Count} paragraphs, {batch[0].Pointer}..{batch[^1].Pointer})."));
            try
            {
                var hits = await ExtractCharactersWithModelAsync(batch, cancellationToken);
                var batchCandidates = hits.Select(CharacterBibleExtractionMapper.ToCandidate).ToList();
                candidates.AddRange(batchCandidates);
                var batchNames = batchCandidates.Count > 0
                    ? ": " + string.Join(", ", batchCandidates.Select(c => c.CanonicalName))
                    : string.Empty;
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "extract",
                    $"Batch {batchNumber} produced {batchCandidates.Count} character candidates{batchNames}."));
            }
            catch (Exception ex) when (IsRecoverableBatchExtractionError(ex))
            {
                failedBatchCount++;
                failedParagraphCount += batch.Count;
                logger.LogError(
                    ex,
                    "Character extraction failed for batch {BatchNumber} ({ParagraphCount} paragraphs, {FirstPointer}..{LastPointer}). Skipping batch.",
                    batchNumber,
                    batch.Count,
                    batch[0].Pointer,
                    batch[^1].Pointer);
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "extract",
                    $"Batch {batchNumber} failed and was skipped ({batch[0].Pointer}..{batch[^1].Pointer})."));
            }
        }

        if (failedBatchCount > 0)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Candidate extraction finished with warnings: {candidates.Count} candidates. Skipped {failedBatchCount} failed batches / {failedParagraphCount} paragraphs."));
        }
        else
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Candidate extraction finished: {candidates.Count} candidates."));
        }

        return candidates;
    }

    private async Task<List<CharacterExtractionCharacter>> ExtractCharactersWithModelAsync(
        IReadOnlyList<(string Pointer, string Text)> paragraphs,
        CancellationToken cancellationToken)
    {
        if (paragraphs.Count == 0)
        {
            return [];
        }

        var extractionResponse = await characterExtractionModelClient.ExtractCharactersAsync(
            new CharacterExtractionModelRequest(
                promptBuilder.BuildSystemPrompt(),
                promptBuilder.BuildUserPrompt(paragraphs)),
            cancellationToken);

        return extractionResponse.Characters
            .Select(CharacterBibleExtractionMapper.NormalizeHit)
            .Where(hit => !string.IsNullOrWhiteSpace(hit.CanonicalName))
            .ToList();
    }

    private static bool IsRecoverableBatchExtractionError(Exception ex)
    {
        return ex is JsonException
            || (ex is InvalidOperationException invalidOperationException
                && string.Equals(invalidOperationException.Message, CharacterExtractionInvalidContractError, StringComparison.Ordinal));
    }
}
