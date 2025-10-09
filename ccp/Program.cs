using System.Text.Json;
using RabbitMQ.Client;
using StackExchange.Redis;
using ccp.Services;
using ccp.Models;
using shared.Messages;
using shared.Services;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Ensure environment variables are loaded
builder.Configuration.AddEnvironmentVariables();

var instanceName = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName;
var otlpEndpoint = builder.Configuration.GetValue<string>("OpenTelemetry:Otlp:Endpoint") ?? "http://localhost:4317";

// Configure structured JSON console logging (Fluentd will parse JSON)
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

// Configure OpenTelemetry for traces only
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService("ccp", "1.0.0")
        .AddAttributes(new Dictionary<string, object>
        {
            ["service.instance.id"] = instanceName,
            ["deployment.environment"] = builder.Environment.EnvironmentName
        }))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                activity.SetTag("http.request_content_length", request.ContentLength);
                activity.SetTag("http.request_content_type", request.ContentType);
            };
            options.EnrichWithHttpResponse = (activity, response) =>
            {
                activity.SetTag("http.response_content_length", response.ContentLength);
                activity.SetTag("http.response_content_type", response.ContentType);
            };
        })
        .AddSource("ccp.*")
        .AddOtlpExporter(otlpOptions =>
        {
            otlpOptions.Endpoint = new Uri(otlpEndpoint);
        }));

// Configure Redis connection
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";
Console.WriteLine($"Redis Connection String from config: '{redisConnectionString}'");
Console.WriteLine($"Environment variable ConnectionString__Redis: '{Environment.GetEnvironmentVariable("ConnectionString__Redis")}'");

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
    // its not the best perfomance, but for demo purposes its ok
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