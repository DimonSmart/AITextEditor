namespace AiTextEditor.Lib.Model;

public class LinearPointer : SemanticPointer
{
    public LinearPointer(int index, SemanticPointer semanticPointer)
        : base(semanticPointer.HeadingTitle, semanticPointer.LineIndex, semanticPointer.CharacterOffset)
    {
        Index = index;
    }

    public int Index { get; }
}
