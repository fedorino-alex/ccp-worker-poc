using System.Collections.Generic;

namespace shared.Models;

public record WorkitemDto
{
    public required string Id { get; set; } // some unique identifier
    public required string Name { get; set; }
    public required Dictionary<string, string> Properties { get; set; }
}