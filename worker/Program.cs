using worker.Services;
using worker.Options;
using System.Text.Json;
using shared.Services;
using RabbitMQ.Client;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Logging
    .ClearProviders()
    .AddConsole();

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

var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(provider => ConnectionMultiplexer.Connect(redisConnectionString));
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

builder.Services.AddSingleton<IControlPlaneChannel, ControlPlaneChannel>();
builder.Services.AddSingleton<IWorkerChannel, WorkerChannel>();
builder.Services.AddSingleton<IRabbitMQClient, RabbitMqClient>();

// Register the hosted service
builder.Services.AddHostedService<WorkerHostedService>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
