namespace AiTextEditor.Lib.Model;

public class LinearPointer : SemanticPointer
{
    public LinearPointer(int index, SemanticPointer semanticPointer)
        : base(semanticPointer.HeadingNumbers, semanticPointer.ParagraphNumber)
    {
        Index = index;
    }

    public int Index { get; }
}
