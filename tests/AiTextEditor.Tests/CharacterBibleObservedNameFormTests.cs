using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Extraction;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterBibleObservedNameFormTests
{
    [Fact]
    public void NormalizeObservedNameForms_DeduplicatesByCase()
    {
        var forms = CharacterBibleExtractionMapper.NormalizeObservedNameForms(
            ["профессор Звездочкин", "Профессор Звездочкин"]);

        var form = Assert.Single(forms);
        Assert.Equal("профессор Звездочкин", form);
    }

    [Fact]
    public void NormalizeObservedNameForms_DeduplicatesByYo()
    {
        var forms = CharacterBibleExtractionMapper.NormalizeObservedNameForms(
            ["Селёдочка", "Селедочка"]);

        var form = Assert.Single(forms);
        Assert.Equal("Селёдочка", form);
    }

    [Fact]
    public void NormalizeObservedNameForms_DeduplicatesByCaseAndYo()
    {
        var forms = CharacterBibleExtractionMapper.NormalizeObservedNameForms(
            ["профессор Звездочкин", "Профессор Звездочкин", "профессор Звёздочкин"]);

        var form = Assert.Single(forms);
        Assert.Equal("профессор Звездочкин", form);
    }

    [Fact]
    public void NormalizeObservedNameForms_PreservesSourceFormText()
    {
        var forms = CharacterBibleExtractionMapper.NormalizeObservedNameForms(
            [" Селёдочка ", "Селедочка"]);

        var form = Assert.Single(forms);
        Assert.Equal("Селёдочка", form);
    }

    [Fact]
    public void ToCandidate_DeduplicatesObservedNameFormDictionariesByCase()
    {
        var candidate = CharacterBibleExtractionMapper.ToCandidate(
            new ExtractedLocalCharacter(
                "Звездочкин",
                "unknown",
                ["профессор Звездочкин", "Профессор Звездочкин"],
                ["1.1.1.p21"]),
            ParagraphsByPointer("1.1.1.p21", "Незнайка увидел профессора Звездочкина."));

        Assert.Single(candidate.ObservedNameFormExamples);
        Assert.Single(candidate.ObservedNameFormEvidence);
        Assert.Contains("профессор Звездочкин", candidate.ObservedNameFormExamples.Keys);
    }

    [Fact]
    public void ToCandidate_DeduplicatesObservedNameFormDictionariesByYo()
    {
        var candidate = CharacterBibleExtractionMapper.ToCandidate(
            new ExtractedLocalCharacter(
                "Селёдочка",
                "unknown",
                ["Селёдочка", "Селедочка"],
                ["1.1.1.p21"]),
            ParagraphsByPointer("1.1.1.p21", "Селёдочка ответила сразу."));

        var example = Assert.Single(candidate.ObservedNameFormExamples);
        var evidence = Assert.Single(candidate.ObservedNameFormEvidence);
        Assert.Equal("Селёдочка", example.Key);
        Assert.Equal("Селёдочка", evidence.Key);
        Assert.Equal("1.1.1.p21", evidence.Value.Pointer);
    }

    [Fact]
    public void CandidatePostProcessor_TreatsCaseAndYoVariantsAsExactRepeats()
    {
        var processor = new CandidatePostProcessor();
        var candidates = processor.Process(
            [
                new ExtractedLocalCharacter(
                    "Звездочкин",
                    "unknown",
                    ["профессор Звездочкин"],
                    ["1.1.1.p21"]),
                new ExtractedLocalCharacter(
                    "ЗВЕЗДОЧКИН",
                    "unknown",
                    ["Профессор Звёздочкин"],
                    ["1.1.1.p21"])
            ],
            [new TextFragment("1.1.1.p21", "профессор Звездочкин вошел.")]);

        Assert.Single(candidates);
    }

    private static IReadOnlyDictionary<string, TextFragment> ParagraphsByPointer(string pointer, string text)
        => new Dictionary<string, TextFragment>(StringComparer.Ordinal)
        {
            [pointer] = new(pointer, text)
        };
}
