namespace shared.Models;

public record PipelineStepDto
{
    public required int Index { get; set; }
    public required string Name { get; set; }
    public bool IsFinal => Next == null;
    public PipelineStepDto? Next { get; set; }
}