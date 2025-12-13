namespace AiTextEditor.Lib.Model;

/// <summary>
/// Represents the type of a linear item in a markdown document.
/// </summary>
public enum LinearItemType
{
    /// <summary>
    /// A heading (e.g., # Title).
    /// </summary>
    Heading,

    /// <summary>
    /// A paragraph of text.
    /// </summary>
    Paragraph,

    /// <summary>
    /// A list item (ordered or unordered).
    /// </summary>
    ListItem,

    /// <summary>
    /// A code block (fenced or indented).
    /// </summary>
    Code,

    /// <summary>
    /// A thematic break (horizontal rule).
    /// </summary>
    ThematicBreak,

    /// <summary>
    /// A block of raw HTML.
    /// </summary>
    Html
}
