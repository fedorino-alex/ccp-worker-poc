using shared.Models;

namespace shared.Messages;

public record ControlPlaneMessage
{
    public required Guid PipelineId { get; set; }
    public string WorkerId { get; set; }
    public WorkitemDto Workitem { get; set; }
    public PipelineStepDto Step { get; set; }
    public required ServiceMessageType MessageType { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum ServiceMessageType
{
    Started,        // Worker has started processing the workitem
    Heartbeat,      // Worker is alive and processing
    Finished        // Worker has completed processing the workitem (no matter success or failure)
}