using AiTextEditor.Core.Model;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterImportanceTests
{
    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(0, "Unknown")]
    [InlineData(1, "Episodic")]
    [InlineData(2, "Minor")]
    [InlineData(4, "Minor")]
    [InlineData(5, "Supporting")]
    [InlineData(7, "Supporting")]
    [InlineData(8, "Main")]
    [InlineData(10, "Main")]
    public void GetLabel_MapsLevelToDisplayLabel(int? level, string expected)
    {
        Assert.Equal(expected, CharacterImportance.GetLabel(level));
    }
}
