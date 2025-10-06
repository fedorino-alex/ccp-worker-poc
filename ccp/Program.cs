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
builder.Services.AddHostedService<ControlPlaneMessagesListener>();
    
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

// return pipeline with active heartbeat
app.MapGet("/pipeline", async Task<string[]> (IPipelineStateService pipelineStateService) =>
{
    List<string> results = new List<string>();

    var pipelineIds = await pipelineStateService.GetHeartbeatPipelinesAsync();
    foreach (var id in pipelineIds)
    {
        var workitem = await pipelineStateService.GetWorkitemAsync(id);
        var step = await pipelineStateService.GetCurrentStepAsync(id);

        results.Add($"{workitem!.Name}, ({id}): {step!.Name}");
    }

    return results.Order().ToArray();
});

// return full details of a specific pipeline
app.MapGet("/pipeline/{pipelineId:guid}", async Task<IResult> (Guid pipelineId, IPipelineStateService pipelineStateService) =>
{
    // check if pipeline is contains in the set of all pipelines
    var heartbeat = await pipelineStateService.GetHeartbeatAsync(pipelineId);
    if (heartbeat is null)
    {
        return Results.NotFound(new { message = "Pipeline not found" });
    }

    var step = await pipelineStateService.GetCurrentStepAsync(pipelineId);
    var workitem = await pipelineStateService.GetWorkitemAsync(pipelineId);

    return Results.Ok(new
    {
        pipelineId,
        workitem,
        heartbeat,
        step
    });
});

// create a new pipeline
app.MapPost("/pipeline", async Task<CreatePipelineResponse> (
    CreatePipelineRequest request,
    IWorkerChannel workerChannel,
    JsonSerializerOptions jsonOptions) =>
{
    // Generate pipeline ID
    var pipelineId = Guid.NewGuid();

    // Deserialize and validate the pipeline schema
    var pipeline = JsonSerializer.Deserialize<PipelineSchema>(request.Schema.Body, jsonOptions)!;

    // Send message to the first worker in the pipeline
    await workerChannel.SendAsync(new WorkerMessage
    {
        PipelineId = pipelineId,
        Workitem = request.Workitem,
        Step = pipeline.Step
    });

    return new CreatePipelineResponse(pipelineId);
});

app.Run();