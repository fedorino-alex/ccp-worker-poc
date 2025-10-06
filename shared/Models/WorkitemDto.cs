namespace shared.Models;

public record WorkitemDto
{
    public required string Id { get; set; } // some unique identifier
    public required string Name { get; set; }
    public Dictionary<string, string>? Properties { get; set; }
    public int RestoreAttempt { get; set; } = 0; // number of times this workitem has been restored
    public int RetryAttempt { get; set; } = 0; // number of times this workitem has been retried (because of exceptions)
}