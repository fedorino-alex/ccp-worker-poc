using System.ComponentModel.DataAnnotations;
using shared.Models;

namespace ccp.Models;

public record CreatePipelineRequest
{
    [Required]
    public required WorkitemDto Workitem { get; set; }

    [Required]
    public required SchemaDto Schema { get; set; }
}