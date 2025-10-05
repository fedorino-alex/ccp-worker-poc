using System.ComponentModel.DataAnnotations;

namespace worker.Options;

public class WorkerOptions
{
    public const string SectionName = "Worker";
    
    [Required]
    public string Name { get; init; }
}