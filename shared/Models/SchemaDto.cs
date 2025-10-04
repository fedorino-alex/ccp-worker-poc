using System.Collections.Generic;

namespace shared.Models;

public record SchemaDto
{
    public required string Body { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}