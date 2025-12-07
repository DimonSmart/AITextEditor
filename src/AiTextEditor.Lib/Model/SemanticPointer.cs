namespace AiTextEditor.Lib.Model;

public class SemanticPointer
{
    public SemanticPointer(IEnumerable<int> headings, int? paragraphNumber)
    {
        HeadingNumbers = headings?.ToList() ?? new List<int>();
        ParagraphNumber = paragraphNumber;
    }

    public IReadOnlyList<int> HeadingNumbers { get; }

    public int? ParagraphNumber { get; }

    public string SemanticNumber
    {
        get
        {
            var prefix = HeadingNumbers.Count == 0
                ? string.Empty
                : string.Join('.', HeadingNumbers);

            if (ParagraphNumber.HasValue)
            {
                var paragraphLabel = $"p{ParagraphNumber.Value}";
                return string.IsNullOrEmpty(prefix)
                    ? paragraphLabel
                    : $"{prefix}.{paragraphLabel}";
            }

            return prefix;
        }
    }
}
