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

        _logger.LogInformation("Worker '{WorkerName}' starting up with instance {InstanceName}",
            _options.Name, Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName);

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
                using var activity = ActivitySource.StartActivity($"Process {workerMessage!.Step.Name} step", ActivityKind.Consumer, parentContext);
                activity?.SetTag("pipeline.id", workerMessage!.PipelineId.ToString());
                activity?.SetTag("workitem.id", workerMessage!.Workitem.Id.ToString());
                activity?.SetTag("step.name", workerMessage!.Step.Name);
                activity?.SetTag("worker.name", _options.Name);

                // Add trace context to log scope for correlation
                using var __ = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["PipelineId"] = workerMessage!.PipelineId.ToString(),
                    ["WorkitemId"] = workerMessage.Workitem.Id.ToString(),
                    ["RetryAttempt"] = workerMessage.Workitem.RetryAttempt.ToString(),
                    ["RestoreAttempt"] = workerMessage.Workitem.RestoreAttempt.ToString(),
                    ["StepName"] = workerMessage.Step.Name,
                    ["WorkerName"] = _options.Name,
                    ["InstanceName"] = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName
                });

                using var heartbeatCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, heartbeatCts.Token);

                try
                {
                    // Process the message
                    await WrapWithControlPlaneEvents(workerMessage, async () =>
                    {
                        await channel.BasicAckAsync(messageResult!.DeliveryTag, false); // acknowledge the message

                        _ = PublishHeartbeat(workerMessage, activity, linkedCts.Token); // start sending heartbeats
                        await ProcessWorkitem(workerMessage.Workitem);
                    }, activity);

                    // If processing succeeded, dispatch to the next step
                    await DispatchToNextStepAsync(workerMessage, activity);
                }
                catch (OperationCanceledException)
                {
                    throw; // let the outer catch handle it
                }
                catch (Exception ex)
                {
                    await HandleProcessingException(workerMessage, ex, activity);
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
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["Exception.Message"] = ex.Message,
                ["Exception.StackTrace"] = ex.StackTrace,
                ["Exception.Type"] = ex.GetType().FullName
            }))
            {
                _logger.LogError(ex, "Error in Worker hosted service '{WorkerName}'", _options.Name);
            }

            throw;
        }
    }

    private async Task DispatchToNextStepAsync(WorkerMessage workerMessage, Activity? parentActivity)
    {
        // If there are more steps, send to the next worker
        if (workerMessage.Step.Next is null)
        {
            _logger.LogInformation("Workitem has completed all steps in the pipeline");
            return;
        }

        using var activity = ActivitySource.StartActivity("Dispatch to Next Step", ActivityKind.Producer, parentActivity!.Context);

        activity?.SetTag("step.name.current", workerMessage.Step.Name);
        activity?.SetTag("step.name.next", workerMessage.Step.Next?.Name ?? string.Empty);
        activity?.SetTag("pipeline.id", workerMessage.PipelineId.ToString());
        activity?.SetTag("workitem.id", workerMessage.Workitem.Id.ToString());

        await _workerChannel.SendAsync(new WorkerMessage
        {
            PipelineId = workerMessage.PipelineId,
            Workitem = workerMessage.Workitem,
            Step = workerMessage.Step.Next!
        }, activity);
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
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["Exception.Message"] = ex.Message,
                ["Exception.StackTrace"] = ex.StackTrace,
                ["Exception.Type"] = ex.GetType().FullName
            }))
            {
                _logger.LogError(ex, "JSON deserialization error for message: {Message}", Encoding.UTF8.GetString(messageResult.Body.ToArray()));
            }

            await channel.BasicNackAsync(messageResult.DeliveryTag, false, false); // reject the message
            return (flowControl: false, value: default);
        }

        return (flowControl: true, value: (messageResult, workerMessage, parentContext));
    }

    private async Task HandleProcessingException(WorkerMessage workerMessage, Exception ex, Activity? activity = null)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["Exception.Message"] = ex.Message,
            ["Exception.StackTrace"] = ex.StackTrace,
            ["Exception.Type"] = ex.GetType().FullName
        }))
        {
            _logger.LogError(ex, "Error processing workitem");
        }

        if (workerMessage.Workitem.RetryAttempt < 10)
        {
            workerMessage.Workitem.RetryAttempt += 1;           // increase retry attempt
            await _workerChannel.SendAsync(new WorkerMessage
            {
                PipelineId = workerMessage.PipelineId,
                Workitem = workerMessage.Workitem,
                Step = workerMessage.Step
            }, activity);

            _logger.LogInformation("Re-queued workitem (RetryAttempt={RetryAttempt})", workerMessage.Workitem.RetryAttempt);
        }
        else
        {
            _logger.LogWarning("Workitem reached max retry attempts (10) and will not be re-queued");

            // TODO: Stop workitem execution, complete pipeline with error
        }
    }

    private async Task WrapWithControlPlaneEvents(WorkerMessage workerMessage, Func<Task> func, Activity? activity = null)
    {
        await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
        {
            PipelineId = workerMessage.PipelineId,
            MessageType = ServiceMessageType.Started,
            Step = workerMessage.Step,
            Workitem = workerMessage.Workitem,
            WorkerId = _options.Name,
            ActivityContext = new WorkerActivityContext(activity!.Id!, activity!.TraceStateString)
        }, activity);

        try
        {
            _logger.LogInformation("Processing of workitem has started");
            await func();
            _logger.LogInformation("Processing of workitem has finished");

            await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
            {
                PipelineId = workerMessage.PipelineId,
                MessageType = ServiceMessageType.Finished,
                Step = workerMessage.Step,
                Workitem = workerMessage.Workitem,
                WorkerId = _options.Name,
                ActivityContext = new WorkerActivityContext(activity!.Id!, activity!.TraceStateString)
            }, activity);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Processing of workitem has failed");

            await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
            {
                PipelineId = workerMessage.PipelineId,
                MessageType = ServiceMessageType.Finished,
                Step = workerMessage.Step,
                Workitem = workerMessage.Workitem,
                WorkerId = _options.Name,
                ErrorMessage = ex.Message,
                ActivityContext = new WorkerActivityContext(activity!.Id!, activity!.TraceStateString)
            }, activity);

            throw;
        }
    }

    private async Task ProcessWorkitem(WorkitemDto workitem)
    {
        // Simulate workitem processing

        //var processingTime = TimeSpan.FromMinutes(new Random().Next(15, 180));
        var processingTime = TimeSpan.FromSeconds(new Random().Next(15, 180));

        var begin = DateTime.UtcNow;
        var end = begin + processingTime;

        while (DateTime.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(5)); // simulate doing some work

            var elapsed = DateTime.UtcNow - begin;
            var progress = Math.Min(100, (int)(elapsed.TotalSeconds / processingTime.TotalSeconds * 100));

            if (elapsed.TotalSeconds > 70)
            {
                throw new Exception("Workitem has simulated processing error.");
            }

            _logger.LogInformation("Still running... Progress: {Progress}%", progress);
        }
    }

    private async Task PublishHeartbeat(WorkerMessage workerMessage, Activity? activity, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); // send heartbeat every 5 seconds
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Stopping heartbeat");
                break;
            }

            _logger.LogDebug("Sending heartbeat");

            await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
            {
                Workitem = workerMessage.Workitem,
                WorkerId = _options.Name,
                Step = workerMessage.Step,
                PipelineId = workerMessage.PipelineId,
                Timestamp = DateTime.UtcNow,
                MessageType = ServiceMessageType.Heartbeat,
                ActivityContext = new WorkerActivityContext(activity!.Id!, activity!.TraceStateString)
            }, activity);
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