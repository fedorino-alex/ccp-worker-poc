using System.Text.Json;
using RabbitMQ.Client;
using StackExchange.Redis;
using ccp.Services;
using ccp.Models;
using shared.Models;
using shared.Messages;
using shared.Services;

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

// Configure RabbitMQ connection
builder.Services.AddSingleton<IConnectionFactory>(provider =>
    new ConnectionFactory
    {
        HostName = builder.Configuration.GetValue<string>("RabbitMQ:HostName") ?? "localhost",
        UserName = builder.Configuration.GetValue<string>("RabbitMQ:UserName") ?? "guest",
        Password = builder.Configuration.GetValue<string>("RabbitMQ:Password") ?? "guest",
        Port = builder.Configuration.GetValue<int?>("RabbitMQ:Port") ?? 5672,
    });

// Configure JSON serialization
var jsonSerializerOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
};

builder.Services.AddSingleton(jsonSerializerOptions);
builder.Services.AddSingleton<IRabbitMQClient, RabbitMqClient>();
builder.Services.AddSingleton<IWorkerChannel, WorkerChannel>();
builder.Services.AddSingleton<IPipelineStateService, PipelineStateService>();

// Register background services
builder.Services.AddHostedService<PipelineHeartbeatMonitorService>();
    
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

// return all pipelines from redis
app.MapGet("/pipeline", async Task<string[]> (IDatabase redis) =>
{
    var pipelineIds = await redis.SetMembersAsync("pipelines:all");
    return pipelineIds.Select(id => id.ToString()).ToArray();
});

app.MapGet("/pipeline/{id:guid}", async Task<IResult> (Guid id, IDatabase redis, JsonSerializerOptions jsonOptions) =>
{
    // check if pipeline is contains in the set of all pipelines
    var isMember = await redis.SetContainsAsync("pipelines:all", id.ToString());
    if (!isMember)
    {
        return Results.NotFound(new { message = "Pipeline not found" });
    }

    var pipelineKey = $"pipeline:{id}";
    var workitem = await redis.StringGetAsync($"{pipelineKey}:workitem");
    var heartbeat = await redis.StringGetAsync($"{pipelineKey}:heartbeat");
    var step = await redis.StringGetAsync($"{pipelineKey}:step");
    var status = await redis.StringGetAsync($"{pipelineKey}:status");
    var worker = await redis.StringGetAsync($"{pipelineKey}:worker");
    var staleAt = await redis.StringGetAsync($"{pipelineKey}:stale_at");
    var staleReason = await redis.StringGetAsync($"{pipelineKey}:stale_reason");

    return Results.Ok(new
    {
        id,
        workitem = workitem.IsNull ? null : JsonSerializer.Deserialize<WorkitemDto>(workitem!, jsonOptions),
        heartbeat = heartbeat.IsNull ? null : heartbeat.ToString(),
        step = step.IsNull ? null : step.ToString(),
        status = status.IsNull ? "active" : status.ToString(),
        worker = worker.IsNull ? null : worker.ToString(),
        staleAt = staleAt.IsNull ? null : staleAt.ToString(),
        staleReason = staleReason.IsNull ? null : staleReason.ToString()
    });
});

// app.MapDelete("/pipeline/{id:guid}", async Task<IResult> (Guid id, IDatabase redis) =>
// {
//     // check if pipeline is contains in the set of all pipelines
//     var isMember = await redis.SetContainsAsync("pipelines:all", id.ToString());
//     if (!isMember)
//     {
//         return Results.NoContent();
//     }

//     // Remove the pipeline from the set of all pipelines
//     await redis.SetRemoveAsync("pipelines:all", id.ToString());
//     // Remove the workitem associated with the pipeline
//     await redis.KeyDeleteAsync($"pipeline:{id}:workitem");

//     return Results.NoContent();
// });

app.MapPost("/pipeline", async Task<CreatePipelineResponse> (
    CreatePipelineRequest request,
    IWorkerChannel pipelineRouter,
    IPipelineStateService pipelineStateService,
    IDatabase redis,
    JsonSerializerOptions jsonOptions) =>
{
    // Generate pipeline ID
    var pipelineId = Guid.NewGuid();
    
    // Deserialize and validate the pipeline schema
    var pipeline = JsonSerializer.Deserialize<PipelineSchema>(request.Schema.Body, jsonOptions)!;

    // Send message to the first worker in the pipeline
    await pipelineRouter.SendAsync(new WorkerMessage
    {
        PipelineId = pipelineId,
        Workitem = request.Workitem,
        Step = pipeline.Step
    });

    // Store the current step in the pipeline state
    await pipelineStateService.PutStepAsync(pipelineId, request.Workitem, pipeline.Step);

    return new CreatePipelineResponse(pipelineId);
});

app.Run();