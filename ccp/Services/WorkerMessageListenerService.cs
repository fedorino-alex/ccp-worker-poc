using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using shared.Messages;
using shared.Services;
using System.Diagnostics;

namespace ccp.Services;

public class ControlPlaneMessagesListener : BackgroundService, IAsyncDisposable
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IPipelineStateService _pipelineStateService;
    private readonly IRabbitMQClient _rabbitMQInfrastructure;
    private readonly ILogger<ControlPlaneMessagesListener> _logger;

    const string ccpControlMessagesQueue = "ccp-control-messages";
    private static readonly ActivitySource ActivitySource = new("Ccp.WorkerMessageListener");

    public ControlPlaneMessagesListener(
        JsonSerializerOptions jsonOptions,
        IPipelineStateService pipelineStateService,
        IRabbitMQClient rabbitMQInfrastructure,
        ILogger<ControlPlaneMessagesListener> logger)
    {
        _jsonOptions = jsonOptions;
        _pipelineStateService = pipelineStateService;
        _rabbitMQInfrastructure = rabbitMQInfrastructure;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker Message Listener Service starting...");

        await _rabbitMQInfrastructure.DeclareQueueAsync(ccpControlMessagesQueue);
        await _rabbitMQInfrastructure.DeclareExchangeAsync(ccpControlMessagesQueue, true);
        await _rabbitMQInfrastructure.DeclareBindingAsync(ccpControlMessagesQueue, ccpControlMessagesQueue, ccpControlMessagesQueue);

        var channel = _rabbitMQInfrastructure.Channel;
        
        // Set up consumer
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                var body = ea.Body.ToArray();

                var activityContext = ExtractTraceContext(ea.BasicProperties.Headers);
                using var activity = ActivitySource.StartActivity("ControlPlaneMessagesListener",
                     ActivityKind.Consumer, activityContext);

                var message = Encoding.UTF8.GetString(body);
                var workerMessage = JsonSerializer.Deserialize<ControlPlaneMessage>(message, _jsonOptions);

                using var _ = _logger.BeginScope(new Dictionary<string, object?>
                {
                    ["PipelineId"] = workerMessage?.PipelineId,
                    ["WorkitemId"] = workerMessage?.Workitem?.Id,
                    ["StepName"] = workerMessage?.Step?.Name,
                    ["MessageType"] = workerMessage?.MessageType.ToString()
                });

                // activity?.SetTag("pipeline.id", workerMessage!.PipelineId.ToString());
                // activity?.SetTag("workitem.id", workerMessage!.Workitem.Id.ToString());
                // activity?.SetTag("step.name", workerMessage!.Step.Name);
                // activity?.SetTag("type", workerMessage!.MessageType.ToString());

                if (workerMessage != null)
                {
                    await ProcessWorkerMessage(workerMessage, activity);

                    // Acknowledge the message
                    await channel.BasicAckAsync(ea.DeliveryTag, false);
                }
                else
                {
                    _logger.LogWarning("Failed to deserialize worker message: {Message}", message);
                    await channel.BasicNackAsync(ea.DeliveryTag, false, false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing worker message");
                await channel.BasicNackAsync(ea.DeliveryTag, false, false);
            }
        };

        await channel.BasicConsumeAsync(
            queue: ccpControlMessagesQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation("Worker Message Listener Service started and listening for messages...");

        try
        {
            // Keep the service running
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker Message Listener Service is stopping due to cancellation...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Worker Message Listener Service");
            throw;
        }
    }

    private async Task ProcessWorkerMessage(ControlPlaneMessage workerMessage, Activity? activity = null)
    {
        _logger.LogInformation("Processing worker message");

        switch (workerMessage.MessageType)
        {
            case ServiceMessageType.Started:
                await HandleStartedMessage(workerMessage);
                break;

            case ServiceMessageType.Heartbeat:
                await HandleHeartbeatMessage(workerMessage, activity);
                break;

            case ServiceMessageType.Finished:
                await HandleFinishedMessage(workerMessage);
                break;

            default:
                _logger.LogWarning("Unknown worker message type: {MessageType}", workerMessage.MessageType);
                break;
        }
    }

    private async Task HandleStartedMessage(ControlPlaneMessage workerMessage)
    {
        _logger.LogInformation("Worker {WorkerId} started processing for pipeline {PipelineId}", 
            workerMessage.WorkerId, workerMessage.PipelineId);

        await _pipelineStateService.PutStepAsync(workerMessage.PipelineId, workerMessage.Workitem, workerMessage.Step);
    }

    private async Task HandleFinishedMessage(ControlPlaneMessage workerMessage)
    {
        if (workerMessage.ErrorMessage is null)
        {
            _logger.LogInformation("Worker {WorkerId} finished step for pipeline {PipelineId}", 
                workerMessage.WorkerId, workerMessage.PipelineId);
        }
        else
        {
            _logger.LogWarning("Worker {WorkerId} finished step for pipeline {PipelineId} with error: {ErrorMessage}", 
                workerMessage.WorkerId, workerMessage.PipelineId, workerMessage.ErrorMessage);
        }

        await _pipelineStateService.DeleteStepAsync(workerMessage.PipelineId, workerMessage.Step);
    }

    private async Task HandleHeartbeatMessage(ControlPlaneMessage workerMessage, Activity? activity = null)
    {
        _logger.LogDebug("Worker {WorkerId} heartbeat for pipeline {PipelineId}", 
            workerMessage.WorkerId, workerMessage.PipelineId);

        activity?.SetTag("heartbeat", workerMessage.Timestamp.ToString("O"));

        // Heartbeat is already handled in ProcessWorkerMessage by updating the heartbeat timestamp
        await _pipelineStateService.PutHeartbeatAsync(workerMessage.PipelineId, workerMessage.Timestamp);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker Message Listener Service is stopping...");

        await base.StopAsync(cancellationToken);

        _logger.LogInformation("Worker Message Listener Service stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_rabbitMQInfrastructure != null)
        {
            await _rabbitMQInfrastructure.DisposeAsync();
        }
    }

    private ActivityContext ExtractTraceContext(IDictionary<string, object?>? headers)
    {
        if (headers == null)
            return default;

        string? traceParent = null;
        string? traceState = null;

        // Extract traceparent header (W3C Trace Context)
        if (headers.TryGetValue("traceparent", out var traceParentObj) && traceParentObj is byte[] traceParentBytes)
        {
            traceParent = Encoding.UTF8.GetString(traceParentBytes);
        }

        // Extract tracestate header (W3C Trace Context vendor-specific data)
        if (headers.TryGetValue("tracestate", out var traceStateObj) && traceStateObj is byte[] traceStateBytes)
        {
            traceState = Encoding.UTF8.GetString(traceStateBytes);
        }

        // Parse both traceparent and tracestate together
        if (!string.IsNullOrEmpty(traceParent))
        {
            if (ActivityContext.TryParse(traceParent, traceState, out var activityContext))
            {
                // Log trace context info for debugging
                _logger.LogDebug("Extracted trace context - TraceId: {TraceId}, SpanId: {SpanId}{TraceState}",
                    activityContext.TraceId, activityContext.SpanId,
                    !string.IsNullOrEmpty(traceState) ? $", TraceState: {traceState}" : "");

                return activityContext;
            }
        }

        return default;
    }

}