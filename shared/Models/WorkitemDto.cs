namespace shared.Models;

public record WorkitemDto
{
    public required string Id { get; set; } // some unique identifier
    public required string Name { get; set; }
    public required Dictionary<string, string> Properties { get; set; }
    public required int RestoreAttempt { get; set; } = 0; // number of times this workitem has been restored
    public required int RetryAttempt { get; set; } = 0; // number of times this workitem has been retried (because of exceptions)
}