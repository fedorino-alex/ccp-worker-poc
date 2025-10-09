using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using shared.Messages;
using shared.Models;
using shared.Services;
using worker.Options;

namespace worker.Services;

public class WorkerHostedService : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("Worker.HostedService");
    
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<WorkerHostedService> _logger;
    private readonly WorkerOptions _options;
    private readonly IRabbitMQClient _client;
    private readonly IControlPlaneChannel _controlPlaneChannel;
    private readonly IWorkerChannel _workerChannel;

    public WorkerHostedService(
        JsonSerializerOptions jsonOptions,
        ILogger<WorkerHostedService> logger,
        IOptions<WorkerOptions> options,
        IControlPlaneChannel controlPlaneChannel,
        IWorkerChannel workerChannel,
        IRabbitMQClient client)
    {
        _jsonOptions = jsonOptions;
        _logger = logger;
        _options = options.Value;
        _client = client;
        _controlPlaneChannel = controlPlaneChannel;
        _workerChannel = workerChannel;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _client.DeclareExchangeAsync(_options.Name, true);
        await _client.DeclareQueueAsync(_options.Name);
        await _client.DeclareBindingAsync(_options.Name, _options.Name, _options.Name);

        _logger.LogInformation("Worker '{WorkerName}' starting up at {Timestamp} with instance {InstanceName}", 
            _options.Name, DateTimeOffset.Now, Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName);

        try
        {
            var channel = _client.Channel;
            await channel.BasicQosAsync(0, 1, true, stoppingToken); // allow only one unacknowledged message at a time

            _logger.LogInformation("Worker '{WorkerName}' is ready and listening for messages", _options.Name);

            while (!stoppingToken.IsCancellationRequested)
            {
                // consume a message
                (var flowControl, (var messageResult, var workerMessage, var parentContext)) = await ConsumeWorkitem(channel, stoppingToken);
                if (flowControl is false)
                {
                    continue;
                }

                // Start activity with parent context from message headers
                using var activity = ActivitySource.StartActivity("ProcessWorkitem", ActivityKind.Consumer, parentContext);
                activity?.SetTag("pipeline.id", workerMessage!.PipelineId.ToString());
                activity?.SetTag("workitem.id", workerMessage!.Workitem.Id.ToString());
                activity?.SetTag("step.name", workerMessage!.Step.Name);
                activity?.SetTag("worker.name", _options.Name);

                // Add trace context to log scope for correlation
                using var logScope = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["TraceId"] = activity?.TraceId.ToString() ?? "unknown",
                    ["SpanId"] = activity?.SpanId.ToString() ?? "unknown",
                    ["PipelineId"] = workerMessage!.PipelineId.ToString(),
                    ["WorkitemId"] = workerMessage.Workitem.Id.ToString(),
                    ["StepName"] = workerMessage.Step.Name,
                    ["WorkerName"] = _options.Name,
                    ["InstanceName"] = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName
                });

                _logger.LogInformation("Received workitem {WorkitemId} for pipeline {PipelineId} at step {StepName}", 
                    workerMessage.Workitem.Id, workerMessage.PipelineId, workerMessage.Step.Name);

                using var heartbeatCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, heartbeatCts.Token);

                _ = PublishHeartbeat(workerMessage!.PipelineId, linkedCts.Token); // start sending heartbeats

                try
                {
                    // Process the message
                    await WrapWithControlPlaneEvents(workerMessage, async () =>
                    {
                        await channel.BasicAckAsync(messageResult!.DeliveryTag, false); // acknowledge the message
                        await ProcessWorkitem(workerMessage.Workitem, stoppingToken);
                    });

                    // If processing succeeded, dispatch to the next step
                    await DispatchToNextStepAsync(workerMessage);
                }
                catch (OperationCanceledException)
                {
                    throw; // let the outer catch handle it
                }
                catch (Exception ex)
                {
                    await HandleProcessingException(workerMessage, ex);
                }
                finally
                {
                    heartbeatCts.Cancel(); // stop sending heartbeats
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker hosted service '{WorkerName}' stopped", _options.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Worker hosted service '{WorkerName}'", _options.Name);
            throw;
        }
    }

    private async Task DispatchToNextStepAsync(WorkerMessage workerMessage)
    {
        // If there are more steps, send to the next worker
        if (workerMessage.Step.Next is not null)
        {
            using var activity = ActivitySource.StartActivity("DispatchToNextStep");
            activity?.SetTag("next.step.name", workerMessage.Step.Next.Name);
            activity?.SetTag("pipeline.id", workerMessage.PipelineId.ToString());
            activity?.SetTag("workitem.id", workerMessage.Workitem.Id.ToString());

            await _workerChannel.SendAsync(new WorkerMessage
            {
                PipelineId = workerMessage.PipelineId,
                Workitem = workerMessage.Workitem,
                Step = workerMessage.Step.Next
            });

            _logger.LogInformation("Forwarded workitem {WorkitemId} for pipeline {PipelineId} to next step {NextStep}",
                workerMessage.Workitem.Id, workerMessage.PipelineId, workerMessage.Step.Next.Name);
        }
    }

    private async Task<(bool flowControl, (BasicGetResult? messageResult, WorkerMessage? workerMessage, ActivityContext parentContext) value)> ConsumeWorkitem(IChannel channel, CancellationToken stoppingToken)
    {
        var messageResult = await channel.BasicGetAsync(_options.Name, false);
        if (messageResult is null)
        {
            _logger.LogDebug("No messages in the queue '{QueueName}'", _options.Name);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // wait before checking again
            return (flowControl: false, value: default);
        }

        // Extract trace context from message headers
        var parentContext = ExtractTraceContext(messageResult.BasicProperties?.Headers);

        WorkerMessage? workerMessage = null;
        try
        {
            var message = messageResult.Body.ToArray();
            workerMessage = JsonSerializer.Deserialize<WorkerMessage>(message, _jsonOptions);
            if (workerMessage is null)
            {
                _logger.LogWarning("Failed to deserialize worker message: {Message}", Encoding.UTF8.GetString(message));
                await channel.BasicNackAsync(messageResult.DeliveryTag, false, false); // reject the message
                return (flowControl: false, value: default);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON deserialization error for message: {Message}", Encoding.UTF8.GetString(messageResult.Body.ToArray()));
            await channel.BasicNackAsync(messageResult.DeliveryTag, false, false); // reject the message
            return (flowControl: false, value: default);
        }

        return (flowControl: true, value: (messageResult, workerMessage, parentContext));
    }

    private async Task HandleProcessingException(WorkerMessage workerMessage, Exception ex)
    {
        Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);

        _logger.LogError(ex, "Error processing workitem {WorkitemId} for pipeline {PipelineId} on step {Step}",
            workerMessage.Workitem.Id, workerMessage.PipelineId, workerMessage.Step.Name);

        // Optionally, you can send a failure message to the control plane here
        await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
        {
            PipelineId = workerMessage.PipelineId,
            MessageType = ServiceMessageType.Finished,
            Step = workerMessage.Step,
            Workitem = workerMessage.Workitem,
            WorkerId = _options.Name,
            ErrorMessage = ex.Message
        });

        if (workerMessage.Workitem.RetryAttempt < 3)
        {
            workerMessage.Workitem.RetryAttempt += 1;           // increase retry attempt
            await _workerChannel.SendAsync(new WorkerMessage
            {
                PipelineId = workerMessage.PipelineId,
                Workitem = workerMessage.Workitem,
                Step = workerMessage.Step
            });

            _logger.LogInformation("Re-queued workitem {WorkitemId} for pipeline {PipelineId} to step {Step} (RetryAttempt={RetryAttempt})",
                workerMessage.Workitem.Id, workerMessage.PipelineId, workerMessage.Step.Name, workerMessage.Workitem.RetryAttempt);
        }
        else
        {
            _logger.LogWarning("Workitem {WorkitemId} for pipeline {PipelineId} reached max retry attempts and will not be re-queued",
                workerMessage.Workitem.Id, workerMessage.PipelineId);

            await _workerChannel.SendAsync(new WorkerMessage
            {
                PipelineId = workerMessage.PipelineId,
                Workitem = workerMessage.Workitem,
                Step = workerMessage.Step
            });

            // TODO: complete pipeline with error
        }
    }

    private async Task WrapWithControlPlaneEvents(WorkerMessage workerMessage, Func<Task> func)
    {
        await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
        {
            PipelineId = workerMessage.PipelineId,
            MessageType = ServiceMessageType.Started,
            Step = workerMessage.Step,
            Workitem = workerMessage.Workitem,
            WorkerId = _options.Name
        });

        _logger.LogInformation("Processing workitem {WorkitemId} for pipeline {PipelineId} on step {Step}",
            workerMessage.Workitem.Id, workerMessage.PipelineId, workerMessage.Step.Name);

        await func();

        _logger.LogInformation("Completed workitem {WorkitemId} for pipeline {PipelineId} on step {Step}",
            workerMessage.Workitem.Id, workerMessage.PipelineId, workerMessage.Step.Name);


        await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
        {
            PipelineId = workerMessage.PipelineId,
            MessageType = ServiceMessageType.Finished,
            Step = workerMessage.Step,
            Workitem = workerMessage.Workitem,
            WorkerId = _options.Name
        });
    }

    private async Task ProcessWorkitem(WorkitemDto workitem, CancellationToken stoppingToken)
    {
        // Simulate workitem processing

        //var processingTime = TimeSpan.FromMinutes(new Random().Next(15, 180));
        var processingTime = TimeSpan.FromSeconds(new Random().Next(15, 180));

        var begin = DateTime.UtcNow;
        var end = begin + processingTime;

        _logger.LogInformation("{Timestamp}: Processing workitem {WorkitemId} for {ProcessingTime}", DateTimeOffset.Now, workitem.Id, processingTime);

        while (DateTime.UtcNow < end && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // simulate doing some work

            var elapsed = DateTime.UtcNow - begin;
            var progress = Math.Min(100, (int)(elapsed.TotalSeconds / processingTime.TotalSeconds * 100));

            if (elapsed.TotalSeconds > 150)
            {
                throw new Exception($"{DateTimeOffset.Now}: Workitem has simulated processing error.");
            }

            _logger.LogInformation("{Timestamp}: Workitem {WorkitemId} progress: {Progress}%", DateTimeOffset.Now, workitem.Id, progress);
        }

        _logger.LogInformation("{Timestamp}: Completed processing workitem {WorkitemId}", DateTimeOffset.Now, workitem.Id);
    }

    private async Task PublishHeartbeat(Guid pipelineId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
            {
                PipelineId = pipelineId,
                MessageType = ServiceMessageType.Heartbeat
            });

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); // send heartbeat every 5 seconds
            }
            catch (TaskCanceledException)
            {
                // Task was canceled, exit the loop
                break;
            }
        }
    }

    private static ActivityContext ExtractTraceContext(IDictionary<string, object?>? headers)
    {
        if (headers == null)
            return default;

        // Extract traceparent header (W3C Trace Context)
        if (headers.TryGetValue("traceparent", out var traceParentObj) && traceParentObj is byte[] traceParentBytes)
        {
            var traceParent = Encoding.UTF8.GetString(traceParentBytes);
            if (ActivityContext.TryParse(traceParent, null, out var activityContext))
            {
                return activityContext;
            }
        }

        return default;
    }
}