using System.Text.Json;
using ccp.Models;
using shared.Messages;
using shared.Services;

namespace ccp.Services;

public class PipelineHeartbeatMonitorService : BackgroundService
{
    private readonly IPipelineStateService _pipelineStateService;
    private readonly IWorkerChannel _workerChannel;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly ILogger<PipelineHeartbeatMonitorService> _logger;

    public PipelineHeartbeatMonitorService(
        IPipelineStateService pipelineStateService,
        ILogger<PipelineHeartbeatMonitorService> logger,
        IWorkerChannel workerChannel,
        JsonSerializerOptions jsonOptions,
        IConfiguration configuration)
    {
        _pipelineStateService = pipelineStateService;
        _logger = logger;
        _workerChannel = workerChannel;
        _jsonOptions = jsonOptions;
        
        // Get configuration values with defaults
        _checkInterval = TimeSpan.FromSeconds(configuration.GetValue<int>("PipelineMonitoring:CheckIntervalSeconds", 10));
        _heartbeatTimeout = TimeSpan.FromSeconds(configuration.GetValue<int>("PipelineMonitoring:HeartbeatTimeoutSeconds", 120)); // 2 minutes default
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Pipeline Heartbeat Monitor Service starting... Check interval: {CheckInterval}, Timeout: {Timeout}", 
            _checkInterval, _heartbeatTimeout);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorPipelineHeartbeats();

                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Pipeline Heartbeat Monitor Service is stopping due to cancellation...");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Pipeline Heartbeat Monitor Service");
                
                // Wait a bit before retrying to avoid rapid fire errors
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task MonitorPipelineHeartbeats()
    {
        _logger.LogDebug("Starting pipeline heartbeat check...");

        try
        {
            // Get all active pipelines
            var pipelineIds = await _pipelineStateService.GetHeartbeatPipelinesAsync();
            if (pipelineIds.Length == 0)
            {
                _logger.LogDebug("No active pipelines found");
                return;
            }

            _logger.LogDebug("Checking heartbeats for {PipelineCount} pipelines", pipelineIds.Length);

            var currentTime = DateTime.UtcNow;
            var stalePipelines = new List<Guid>();
            var activePipelines = 0;
            var completedPipelines = 0;

            foreach (var pipelineId in pipelineIds)
            {
                // Check if pipeline has a current step (indicating it's active)
                var heartbeatRecord = await _pipelineStateService.GetHeartbeatAsync(pipelineId);
                if (heartbeatRecord is null)
                {
                    // Pipeline might be completed
                    completedPipelines++;
                    continue;
                }

                activePipelines++;

                if (DateTime.TryParse(heartbeatRecord.ToString(), out var heartbeatTimestamp))
                {
                    var timeSinceLastHeartbeat = currentTime - heartbeatTimestamp;
                    if (timeSinceLastHeartbeat > _heartbeatTimeout)
                    {
                        stalePipelines.Add(pipelineId);

                        _logger.LogWarning("Pipeline {PipelineId} is stale. Last heartbeat: {LastHeartbeat}, Time since: {TimeSince}, Step: {Step}",
                            pipelineId, heartbeatTimestamp, timeSinceLastHeartbeat, heartbeatRecord);
                    }
                    else
                    {
                        _logger.LogDebug("Pipeline {PipelineId} is healthy. Last heartbeat: {LastHeartbeat}, Step: {Step}",
                            pipelineId, heartbeatTimestamp, heartbeatRecord);
                    }
                }
                else
                {
                    _logger.LogError("Pipeline {PipelineId} has invalid heartbeat timestamp: {Heartbeat}", 
                        pipelineId, heartbeatRecord.ToString());
                }
            }

            // Log summary
            _logger.LogInformation("Pipeline status: {TotalPipelines} total, {ActivePipelines} active, {CompletedPipelines} completed/inactive, {StalePipelines} stale",
                pipelineIds.Length, activePipelines, completedPipelines, stalePipelines.Count);

            // Handle stale pipelines
            if (stalePipelines.Count > 0)
            {
                await HandleStalePipelines(stalePipelines);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring pipeline heartbeats");
        }
    }

    private async Task HandleStalePipelines(List<Guid> stalePipelineIds)
    {
        _logger.LogWarning("Handling {Count} stale pipelines", stalePipelineIds.Count);

        foreach (var pipelineId in stalePipelineIds)
        {
            try
            {
                await HandleStalePipeline(pipelineId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling stale pipeline {PipelineId}", pipelineId);
            }
        }
    }

    private async Task HandleStalePipeline(Guid pipelineId)
    {
        // take step info and delete key atomicly, so no else ccp can take it and restart work twice
        var currentStep = await _pipelineStateService.LockCurrentStepAsync(pipelineId);
        if (currentStep is null)
        {
            _logger.LogWarning("Stale pipeline {PipelineId} will be restarted by else ccp", pipelineId);
            return;
        }

        var workitem = await _pipelineStateService.GetWorkitemAsync(pipelineId);
        workitem!.RestoreAttempt += 1;  // increase attempt count for restores

        // remove stale pipeline from monitoring heartbeat
        await _pipelineStateService.DeleteStepAsync(pipelineId, currentStep!);

        // re-queue workitem to worker channel
        await _workerChannel.SendAsync(new WorkerMessage
        {
            PipelineId = pipelineId,
            Workitem = workitem!,
            Step = currentStep!
        });

        _logger.LogInformation("Restarted stale pipeline {PipelineId} at step {Step} with {RestoreAttempt} attempt", pipelineId, currentStep!.Name, workitem!.RestoreAttempt);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pipeline Heartbeat Monitor Service is stopping...");
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("Pipeline Heartbeat Monitor Service stopped.");
    }
}