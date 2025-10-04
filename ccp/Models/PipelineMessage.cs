using shared.Models;

namespace ccp.Models;

public record PipelineMessage
{
    public required Guid PipelineId { get; set; }
    public required WorkitemDto Workitem { get; set; }
    public required PipelineStepDto Step { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}