using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using shared.Messages;
using shared.Models;
using shared.Services;
using worker.Options;

namespace worker.Services;

public class WorkerHostedService : BackgroundService
{
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

        _logger.LogInformation("Worker '{WorkerName}' is running at: {time}", _options.Name, DateTimeOffset.Now);

        try
        {
            var channel = _client.Channel;
            await channel.BasicQosAsync(0, 1, true, stoppingToken); // allow only one unacknowledged message at a time

            while (!stoppingToken.IsCancellationRequested)
            {
                var messageResult = await channel.BasicGetAsync(_options.Name, false);
                if (messageResult is null)
                {
                    _logger.LogInformation("No messages in the queue '{QueueName}'", _options.Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // wait before checking again
                    continue;
                }

                var message = messageResult.Body.ToArray();
                var workerMessage = JsonSerializer.Deserialize<WorkerMessage>(message, _jsonOptions);
                if (workerMessage is null)
                {
                    _logger.LogWarning("Failed to deserialize worker message: {Message}", Encoding.UTF8.GetString(message));
                    await channel.BasicNackAsync(messageResult.DeliveryTag, false, false);
                    continue;
                }

                using var heartbeatCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, heartbeatCts.Token);

                _ = PublishHeartbeat(workerMessage.PipelineId, linkedCts.Token); // start sending heartbeats

                try
                {
                    await _controlPlaneChannel.SendAsync(new ControlPlaneMessage
                    {
                        PipelineId = workerMessage.PipelineId,
                        MessageType = ServiceMessageType.Started,
                        Step = workerMessage.Step,
                        Workitem = workerMessage.Workitem,
                        WorkerId = _options.Name
                    });

                    await channel.BasicAckAsync(messageResult.DeliveryTag, false);
                    _logger.LogInformation("Processing workitem {WorkitemId} for pipeline {PipelineId} on step {Step}",
                        workerMessage.Workitem.Id, workerMessage.PipelineId, workerMessage.Step.Name);

                    // Process the message
                    await ProcessWorkitem(workerMessage.Workitem, stoppingToken);

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

                    // If there are more steps, send to the next worker
                    if (workerMessage.Step.Next is not null)
                    {
                        await _workerChannel.SendAsync(new WorkerMessage
                        {
                            PipelineId = workerMessage.PipelineId,
                            Workitem = workerMessage.Workitem,
                            Step = workerMessage.Step
                        });

                        _logger.LogInformation("Forwarded workitem {WorkitemId} for pipeline {PipelineId} to next step {NextStep}",
                            workerMessage.Workitem.Id, workerMessage.PipelineId, workerMessage.Step.Next.Name);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // let the outer catch handle it
                }
                catch (Exception ex)
                {
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

    private async Task ProcessWorkitem(WorkitemDto workitem, CancellationToken stoppingToken)
    {
        // Simulate workitem processing

        var processingTime = TimeSpan.FromMinutes(new Random().Next(15, 180));
        //var processingTime = TimeSpan.FromSeconds(new Random().Next(15, 180));

        var begin = DateTime.UtcNow;
        var end = begin + processingTime;

        _logger.LogInformation("Processing workitem {WorkitemId} for {ProcessingTime}", workitem.Id, processingTime);

        while (DateTime.UtcNow < end && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // simulate doing some work

            var elapsed = DateTime.UtcNow - begin;
            var progress = Math.Min(100, (int)(elapsed.TotalSeconds / processingTime.TotalSeconds * 100));

            _logger.LogInformation("{Timestamp}: Workitem {WorkitemId} progress: {Progress}%", DateTime.UtcNow.ToString("O"), workitem.Id, progress);
        }

        _logger.LogInformation("Completed processing workitem {WorkitemId}", workitem.Id);
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
}