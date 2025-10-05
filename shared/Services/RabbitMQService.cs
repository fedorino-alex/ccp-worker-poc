using RabbitMQ.Client;
using System.Text.Json;

namespace shared.Services;

public interface IRabbitMQClient : IAsyncDisposable
{
    IChannel Channel { get; }

    Task DeclareQueueAsync(string queueName, bool durable = true);
    Task DeclareExchangeAsync(string exchangeName, bool durable);
    Task DeclareBindingAsync(string exchangeName, string queueName, string routingKey);
}

public class RabbitMqClient : IRabbitMQClient
{
    private readonly IConnectionFactory _connectionFactory;
    private IConnection _connection;
    private IChannel _channel;

    public IChannel Channel => _channel;

    public RabbitMqClient(IConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task DeclareExchangeAsync(string exchangeName, bool durable = true)
    {
        if (_connection?.IsOpen != true)
        {
            _connection = await _connectionFactory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();
        }

        await _channel.ExchangeDeclareAsync(
            exchange: exchangeName,
            type: ExchangeType.Direct,
            durable: durable,
            autoDelete: false,
            arguments: null);
    }

    public async Task DeclareQueueAsync(string queueName, bool durable = true)
    {
        if (_connection?.IsOpen != true)
        {
            _connection = await _connectionFactory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();
        }

        await _channel.QueueDeclareAsync(
            queue: queueName,
            durable: durable,
            exclusive: false,
            autoDelete: false,
            arguments: null);
    }

    public async Task DeclareBindingAsync(string exchangeName, string queueName, string routingKey)
    {
        if (_connection?.IsOpen != true)
        {
            _connection = await _connectionFactory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();
        }

        await _channel.QueueBindAsync(queueName, exchangeName, routingKey);
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();

        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}