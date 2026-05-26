using System.Text.Json;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible.Extraction;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class StructuredJsonResponseRecoveryTests
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonSerializerOptions.Web;

    [Fact]
    public void TryRecover_WithCleanJson_ReturnsResponse()
    {
        var recovered = StructuredJsonResponseRecovery.TryRecover<CharacterExtractionResponse>(
            """
            { "characters": [] }
            """,
            SerializerOptions,
            out var response,
            out var extractedJson,
            out var error);

        Assert.True(recovered);
        Assert.NotNull(response);
        Assert.Empty(response.Characters);
        Assert.NotNull(extractedJson);
        Assert.Null(error);
    }

    [Fact]
    public void TryRecover_WithMarkdownFence_ReturnsExtractedResponse()
    {
        var recovered = StructuredJsonResponseRecovery.TryRecover<CharacterExtractionResponse>(
            """
            ```json
            { "characters": [] }
            ```
            """,
            SerializerOptions,
            out var response,
            out var extractedJson,
            out var error);

        Assert.True(recovered);
        Assert.NotNull(response);
        Assert.Empty(response.Characters);
        Assert.Equal("""{ "characters": [] }""", extractedJson);
        Assert.Null(error);
    }

    [Fact]
    public void TryRecover_WithPrefixAndSuffixText_ReturnsExtractedResponse()
    {
        var recovered = StructuredJsonResponseRecovery.TryRecover<CharacterExtractionResponse>(
            """
            Here is the JSON:
            { "characters": [] }
            Done.
            """,
            SerializerOptions,
            out var response,
            out var extractedJson,
            out var error);

        Assert.True(recovered);
        Assert.NotNull(response);
        Assert.Empty(response.Characters);
        Assert.Equal("""{ "characters": [] }""", extractedJson);
        Assert.Null(error);
    }

    [Fact]
    public void TryRecover_WithMultipleJsonFragments_ReturnsLongestValidFragment()
    {
        var recovered = StructuredJsonResponseRecovery.TryRecover<CharacterExtractionResponse>(
            """
            { "characters": [] }
            Later:
            {
              "characters": [
                {
                  "canonicalName": "John",
                  "gender": "unknown",
                  "aliases": [],
                  "profile": {
                    "appearance": "",
                    "statusAndCompetence": "",
                    "psychologicalProfile": "",
                    "speechAndCommunication": ""
                  }
                }
              ]
            }
            """,
            SerializerOptions,
            out var response,
            out var extractedJson,
            out var error);

        Assert.True(recovered);
        var character = Assert.Single(response!.Characters);
        Assert.Equal("John", character.CanonicalName);
        Assert.NotNull(extractedJson);
        Assert.Contains("canonicalName", extractedJson, StringComparison.Ordinal);
        Assert.Null(error);
    }

    [Fact]
    public void TryRecover_WithoutValidJson_ReturnsFalse()
    {
        var recovered = StructuredJsonResponseRecovery.TryRecover<CharacterExtractionResponse>(
            "There is no structured data here.",
            SerializerOptions,
            out var response,
            out var extractedJson,
            out var error);

        Assert.False(recovered);
        Assert.Null(response);
        Assert.Null(extractedJson);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryRecover_WithTruncatedJson_ReturnsFalse()
    {
        var recovered = StructuredJsonResponseRecovery.TryRecover<CharacterExtractionResponse>(
            """
            { "characters": [
            """,
            SerializerOptions,
            out var response,
            out var extractedJson,
            out var error);

        Assert.False(recovered);
        Assert.Null(response);
        Assert.Null(extractedJson);
        Assert.NotNull(error);
    }
}
