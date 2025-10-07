using RabbitMQ.Client;
using shared.Messages;
using System.Text.Json;
using System.Diagnostics;
using System.Text;

namespace shared.Services;

public interface IWorkerChannel
{
    Task SendAsync(WorkerMessage message);
    Task DeadLetterAsync(WorkerMessage message);
}

public class WorkerChannel : IWorkerChannel
{
    private readonly IRabbitMQClient _rabbitMQInfrastructure;
    private readonly JsonSerializerOptions _jsonOptions;

    public WorkerChannel(IRabbitMQClient rabbitMQInfrastructure, JsonSerializerOptions jsonOptions)
    {
        _rabbitMQInfrastructure = rabbitMQInfrastructure;
        _jsonOptions = jsonOptions;
    }

    public async Task DeadLetterAsync(WorkerMessage message)
    {
        var stepDlx = $"{message.Step.Name}-dlx";

        await _rabbitMQInfrastructure.DeclareExchangeAsync(stepDlx, true);
        await _rabbitMQInfrastructure.DeclareQueueAsync(stepDlx);
        await _rabbitMQInfrastructure.DeclareBindingAsync(stepDlx, stepDlx, stepDlx);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
        
        // Create basic properties and inject trace context
        var properties = new BasicProperties();
        InjectTraceContext(properties);
        
        await _rabbitMQInfrastructure.Channel.BasicPublishAsync(
            exchange: stepDlx,                        // use step name as exchange and queue name
            routingKey: stepDlx,                      // assuming step.Name is the queue name
            mandatory: false,
            basicProperties: properties,
            body: new ReadOnlyMemory<byte>(bytes));
    }

    public async Task SendAsync(WorkerMessage message)
    {
        var stepName = message.Step.Name;
        
        // Ensure the exchange, queue, and binding exist
        await _rabbitMQInfrastructure.DeclareExchangeAsync(stepName, true);
        await _rabbitMQInfrastructure.DeclareQueueAsync(stepName);
        await _rabbitMQInfrastructure.DeclareBindingAsync(stepName, stepName, stepName);

        // Send the message
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);

        // Create basic properties and inject trace context
        var properties = new BasicProperties();
        InjectTraceContext(properties);

        await _rabbitMQInfrastructure.Channel.BasicPublishAsync(
            exchange: stepName,                        // use step name as exchange and queue name
            routingKey: stepName,                      // assuming step.Name is the queue name
            mandatory: false,
            basicProperties: properties,
            body: new ReadOnlyMemory<byte>(bytes));
    }

    private static void InjectTraceContext(BasicProperties properties)
    {
        var currentActivity = Activity.Current;
        if (currentActivity != null)
        {
            properties.Headers ??= new Dictionary<string, object?>();
            
            // Inject W3C Trace Context
            var traceParent = currentActivity.Id;
            if (!string.IsNullOrEmpty(traceParent))
            {
                properties.Headers["traceparent"] = Encoding.UTF8.GetBytes(traceParent);
            }

            // Inject trace state if available
            var traceState = currentActivity.TraceStateString;
            if (!string.IsNullOrEmpty(traceState))
            {
                properties.Headers["tracestate"] = Encoding.UTF8.GetBytes(traceState);
            }
        }
    }
}
