using RabbitMQ.Client;
using shared.Messages;
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);

        await _rabbitMQInfrastructure.Channel.BasicPublishAsync(
            exchange: ccpControlMessagesQueue,                        // use step name as exchange and queue name
            routingKey: ccpControlMessagesQueue,                      // assuming step.Name is the queue name
            body: new ReadOnlyMemory<byte>(bytes));
    }
}
