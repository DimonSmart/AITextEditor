using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class MarkdownLinearizationTests
{
    [Fact]
    public void BuildLinearDocument_AssignsPointersWithOffsets()
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
                Assert.Equal("{\"Id\":1,\"Label\":\"1\"}", item.Pointer.Serialize());
                Assert.Equal("Title", item.Text);
            },
            item =>
            {
                Assert.Equal(1, item.Index);
                Assert.Equal(LinearItemType.Paragraph, item.Type);
                Assert.Equal("{\"Id\":2,\"Label\":\"1.p1\"}", item.Pointer.Serialize());
                Assert.Equal("Intro paragraph", item.Text);
            },
            item =>
            {
                Assert.Equal(2, item.Index);
                Assert.Equal(LinearItemType.Heading, item.Type);
                Assert.Equal("{\"Id\":3,\"Label\":\"1.1\"}", item.Pointer.Serialize());
                Assert.Equal("Section", item.Text);
            },
            item =>
            {
                Assert.Equal(3, item.Index);
                Assert.Equal(LinearItemType.Paragraph, item.Type);
                Assert.Equal("{\"Id\":4,\"Label\":\"1.1.p1\"}", item.Pointer.Serialize());
                Assert.Equal("Paragraph inside section", item.Text);
            },
            item =>
            {
                Assert.Equal(4, item.Index);
                Assert.Equal(LinearItemType.Paragraph, item.Type);
                Assert.Equal("{\"Id\":5,\"Label\":\"1.1.p2\"}", item.Pointer.Serialize());
                Assert.Equal("Another paragraph", item.Text);
            });
    }

    [Fact]
    public void ListItems_TrackLineOffsets()
    {
        var markdown = "# Title\n\n- First item\n- Second item\n";
        var repository = new MarkdownDocumentRepository();

        var document = repository.LoadFromMarkdown(markdown);

        Assert.Equal(3, document.Items.Count);
        Assert.Equal("{\"Id\":1,\"Label\":\"1\"}", document.Items[0].Pointer.Serialize());
        Assert.Equal(LinearItemType.ListItem, document.Items[1].Type);
        Assert.Equal("{\"Id\":2,\"Label\":\"1.p1\"}", document.Items[1].Pointer.Serialize());
        Assert.Equal("{\"Id\":3,\"Label\":\"1.p2\"}", document.Items[2].Pointer.Serialize());
        Assert.Equal("Second item", document.Items[2].Text);
    }
}
