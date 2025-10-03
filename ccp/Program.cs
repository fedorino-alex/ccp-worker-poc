using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Configure Redis connection
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(provider =>
    ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddSingleton(provider =>
{
    var connection = provider.GetRequiredService<IConnectionMultiplexer>();
    return connection.GetDatabase();
});

// Add services for automatic model validation
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
});

var jsonSerializerOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
};

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

// return all pipelines from redis
app.MapGet("/pipeline", async Task<string[]> (IDatabase redis) =>
{
    var pipelineIds = await redis.SetMembersAsync("pipelines:all");
    return pipelineIds.Select(id => id.ToString()).ToArray();
});

app.MapGet("/pipeline/{id:guid}", async Task<IResult> (Guid id, IDatabase redis) =>
{
    // check if pipeline is contains in the set of all pipelines
    var isMember = await redis.SetContainsAsync("pipelines:all", id.ToString());
    if (!isMember)
    {
        return Results.NotFound(new { message = "Pipeline not found" });
    }

    var pipelineData = await redis.StringGetAsync($"pipeline:{id}:body");
    if (!pipelineData.HasValue)
    {
        return Results.NotFound(new { message = "Pipeline not found" });
    }
    
    // Get pipeline metadata
    var metadata = await redis.HashGetAllAsync($"pipeline:{id}:metadata");
    var metadataDict = metadata.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString());
    
    return Results.Ok(new 
    { 
        id,
        metadata = metadataDict,
        data = JsonSerializer.Deserialize<CreatePipelineRequest>(pipelineData!, jsonSerializerOptions)
    });
});

app.MapPost("/pipeline", async Task<CreatePipelineResponse> (
    CreatePipelineRequest request,
    IDatabase redis) =>
{
    // Deserialize and validate the pipeline schema
    var pipeline = JsonSerializer.Deserialize<PipelineSchema>(request.Schema.Body, jsonSerializerOptions);

    // Generate pipeline ID
    var pipelineId = Guid.NewGuid();

    // Store pipeline body in Redis to be able pick it up when needed
    await redis.StringSetAsync($"pipeline:{pipelineId}:body", request.Schema.Body);

    // Store pipeline metadata
    await redis.HashSetAsync($"pipeline:{pipelineId}:metadata",
    [
        new ("id", request.Workitem.Id),
        new ("name", request.Workitem.Name),
        new ("createdAt", DateTimeOffset.UtcNow.ToString("O"))
    ]);

    // add pipeline to a set of all pipelines
    await redis.SetAddAsync("pipelines:all", pipelineId.ToString());

    // Simulate some processing delay
    await Task.Delay(TimeSpan.FromMilliseconds(1_00 * Random.Shared.NextDouble()));

    return new CreatePipelineResponse(pipelineId);
});

app.Run();

// Validation filter for automatic validation
public record CreatePipelineRequest
{
    [Required]
    public required WorkitemDto Workitem { get; set; }

    [Required]
    public required SchemaDto Schema { get; set; }
}

public record CreatePipelineResponse(Guid PipelineId);

public record WorkitemDto
{
    public required string Id { get; set; } // some unique identifier
    public required string Name { get; set; }
    public required Dictionary<string, string> Properties { get; set; }
}

public record SchemaDto
{
    public required string Body { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}

public class PipelineSchema
{
    public required PipelineStep Step { get; set; }
}

public record PipelineStep
{
    public required int Index { get; set; }
    public required string Name { get; set; }
    public bool IsFinal => Next == null;
    public PipelineStep? Next { get; set; }
}