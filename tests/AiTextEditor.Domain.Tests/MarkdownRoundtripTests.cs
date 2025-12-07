using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class MarkdownRoundtripTests
{
    public static IEnumerable<object[]> BookExamples => Directory
        .EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownBooks"), "*.md")
        .Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(BookExamples))]
    public void ReadThenWrite_DoesNotAlterMarkdown(string path)
    {
        var repository = new MarkdownDocumentRepository();
        var originalContent = File.ReadAllText(path);

        var document = repository.LoadFromMarkdownFile(path);
        var rewritten = repository.WriteToMarkdown(document);

        var normalizedOriginal = Normalize(originalContent);

        Assert.Equal(normalizedOriginal, document.SourceText);
        Assert.Equal(normalizedOriginal, rewritten);
    }

    private static string Normalize(string content)
    {
        return content.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
