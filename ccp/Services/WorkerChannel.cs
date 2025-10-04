using RabbitMQ.Client;
using shared.Messages;
using shared.Services;
using System.Text.Json;

namespace ccp.Services;

public interface IWorkerChannel
{
    Task SendAsync(WorkerMessage message);
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

    public async Task SendAsync(WorkerMessage message)
    {
        var stepName = message.Step.Name;
        
        // Ensure the exchange, queue, and binding exist
        await _rabbitMQInfrastructure.DeclareExchangeAsync(stepName, true);
        await _rabbitMQInfrastructure.DeclareQueueAsync(stepName);
        await _rabbitMQInfrastructure.DeclareBindingAsync(stepName, stepName, stepName);

        // Send the message
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);

        await _rabbitMQInfrastructure.Channel.BasicPublishAsync(
            exchange: stepName,                        // use step name as exchange and queue name
            routingKey: stepName,                      // assuming step.Name is the queue name
            body: new ReadOnlyMemory<byte>(bytes));
    }
}
