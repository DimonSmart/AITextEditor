namespace AiTextEditor.Lib.Model;

public record BulkOperationProgress(int Completed, int Total)
{
    public double Percentage => Total == 0 ? 1 : (double)Completed / Total;
}
