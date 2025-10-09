using worker.Services;
using worker.Options;
using System.Text.Json;
using shared.Services;
using RabbitMQ.Client;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var workerName = builder.Configuration.GetValue<string>("Worker:Name")!;
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
        .AddService(workerName)
        .AddAttributes(new Dictionary<string, object>
        {
            ["worker.name"] = workerName,
            ["service.instance.id"] = instanceName,
            ["deployment.environment"] = builder.Environment.EnvironmentName
        }))
    .WithTracing(tracing => tracing
        .AddSource("worker.*")
        .AddOtlpExporter(otlpOptions =>
        {
            otlpOptions.Endpoint = new Uri(otlpEndpoint);
        }));

var jsonSerializerOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
};

builder.Services.AddSingleton(jsonSerializerOptions);

// Configure WorkerOptions
builder.Services
    .AddOptions<WorkerOptions>()
    .BindConfiguration(WorkerOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Configure RabbitMQ connection
builder.Services.AddSingleton<IConnectionFactory>(provider =>
    new ConnectionFactory
    {
        HostName = builder.Configuration.GetValue<string>("RabbitMQ:HostName") ?? "localhost",
        UserName = builder.Configuration.GetValue<string>("RabbitMQ:UserName") ?? "guest",
        Password = builder.Configuration.GetValue<string>("RabbitMQ:Password") ?? "guest",
        Port = builder.Configuration.GetValue<int?>("RabbitMQ:Port") ?? 5672,
    });

builder.Services.AddSingleton<IControlPlaneChannel, ControlPlaneChannel>();
builder.Services.AddSingleton<IWorkerChannel, WorkerChannel>();
builder.Services.AddSingleton<IRabbitMQClient, RabbitMqClient>();

// Register the hosted service
builder.Services.AddHostedService<WorkerHostedService>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
