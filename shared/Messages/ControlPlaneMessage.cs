using shared.Models;

namespace shared.Messages;

public record ControlPlaneMessage
{
    public required Guid PipelineId { get; set; }
    public required string WorkerId { get; set; }
    public required WorkitemDto Workitem { get; set; }
    public required PipelineStepDto Step { get; set; }
    public required ServiceMessageType MessageType { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum ServiceMessageType
{
    Started,        // Worker has started processing the workitem
    Heartbeat,      // Worker is alive and processing
    Finished       // Worker has completed processing the workitem (no matter success or failure)
}