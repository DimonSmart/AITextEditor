using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class MarkdownRoundtripTests
{
    public static TheoryData<string> BookExamples
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownBooks"), "*.md"))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BookExamples))]
    public void ReadThenWrite_DoesNotAlterMarkdown(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownBooks", fileName);
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
