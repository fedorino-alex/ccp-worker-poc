using RabbitMQ.Client;
using shared.Messages;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace shared.Services;

public interface IControlPlaneChannel
{
    Task SendAsync(ControlPlaneMessage message);
}

public class ControlPlaneChannel : IControlPlaneChannel
{
    const string ccpControlMessagesQueue = "ccp-control-messages";

    private readonly IRabbitMQClient _rabbitMQInfrastructure;
    private readonly JsonSerializerOptions _jsonOptions;

    public ControlPlaneChannel(IRabbitMQClient rabbitMQInfrastructure, JsonSerializerOptions jsonOptions)
    {
        _rabbitMQInfrastructure = rabbitMQInfrastructure;
        _jsonOptions = jsonOptions;
    }

    public async Task SendAsync(ControlPlaneMessage message)
    {
        // Ensure the exchange, queue, and binding exist
        await _rabbitMQInfrastructure.DeclareExchangeAsync(ccpControlMessagesQueue, true);
        await _rabbitMQInfrastructure.DeclareQueueAsync(ccpControlMessagesQueue);
        await _rabbitMQInfrastructure.DeclareBindingAsync(ccpControlMessagesQueue, ccpControlMessagesQueue, ccpControlMessagesQueue);

        // Send the message
        var properties = new BasicProperties();
        InjectTraceContext(properties);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);

        await _rabbitMQInfrastructure.Channel.BasicPublishAsync(
            exchange: ccpControlMessagesQueue,                        // use step name as exchange and queue name
            routingKey: ccpControlMessagesQueue,                      // assuming step.Name is the queue name
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
