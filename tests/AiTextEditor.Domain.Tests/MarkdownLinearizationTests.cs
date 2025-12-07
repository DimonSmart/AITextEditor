using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class MarkdownLinearizationTests
{
    [Fact]
    public void BuildLinearDocument_AssignsHeadingAndParagraphNumbers()
    {
        var markdown = "# Title\n\nIntro paragraph\n\n## Section\n\nParagraph inside section\n\nAnother paragraph";
        var repository = new MarkdownDocumentRepository();

        var document = repository.LoadFromMarkdown(markdown);

        Assert.Collection(
            document.Items,
            item =>
            {
                Assert.Equal(0, item.Index);
                Assert.Equal(LinearItemType.Heading, item.Type);
                Assert.Equal("1", item.Pointer.SemanticNumber);
                Assert.Equal("Title", item.Text);
            },
            item =>
            {
                Assert.Equal(1, item.Index);
                Assert.Equal(LinearItemType.Paragraph, item.Type);
                Assert.Equal("1.p1", item.Pointer.SemanticNumber);
                Assert.Equal("Intro paragraph", item.Text);
            },
            item =>
            {
                Assert.Equal(2, item.Index);
                Assert.Equal(LinearItemType.Heading, item.Type);
                Assert.Equal("1.1", item.Pointer.SemanticNumber);
                Assert.Equal("Section", item.Text);
            },
            item =>
            {
                Assert.Equal(3, item.Index);
                Assert.Equal(LinearItemType.Paragraph, item.Type);
                Assert.Equal("1.1.p1", item.Pointer.SemanticNumber);
                Assert.Equal("Paragraph inside section", item.Text);
            },
            item =>
            {
                Assert.Equal(4, item.Index);
                Assert.Equal(LinearItemType.Paragraph, item.Type);
                Assert.Equal("1.1.p2", item.Pointer.SemanticNumber);
                Assert.Equal("Another paragraph", item.Text);
            });
    }

    [Fact]
    public void ListItems_IncrementParagraphCounter()
    {
        var markdown = "# Title\n\n- First item\n- Second item\n";
        var repository = new MarkdownDocumentRepository();

        var document = repository.LoadFromMarkdown(markdown);

        Assert.Equal(3, document.Items.Count);
        Assert.Equal("1", document.Items[0].Pointer.SemanticNumber);
        Assert.Equal(LinearItemType.ListItem, document.Items[1].Type);
        Assert.Equal("1.p1", document.Items[1].Pointer.SemanticNumber);
        Assert.Equal("1.p2", document.Items[2].Pointer.SemanticNumber);
        Assert.Equal("Second item", document.Items[2].Text);
    }
}
