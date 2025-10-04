using shared.Models;

namespace shared.Messages;

public record WorkerMessage
{
    public required Guid PipelineId { get; set; }
    public required WorkitemDto Workitem { get; set; }
    public required PipelineStepDto Step { get; set; }
    public int RetryCount { get; set; } = 0;
}