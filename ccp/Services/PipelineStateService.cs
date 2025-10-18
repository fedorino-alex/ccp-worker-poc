using StackExchange.Redis;
using System.Text.Json;
using shared.Models;
using shared.Messages;

namespace ccp.Services;

public interface IPipelineStateService
{
    Task PutStepAsync(Guid pipelineId, WorkitemDto workitem, PipelineStepDto step, WorkerActivityContext activityContext);
    Task PutHeartbeatAsync(Guid pipelineId, DateTime timestamp);
    Task DeleteStepAsync(Guid pipelineId, PipelineStepDto step);

    Task<Guid[]> GetHeartbeatPipelinesAsync(); // List all heartbeat pipelines
    Task<string?> GetHeartbeatAsync(Guid pipelineId); // Get heartbeat for a specific pipeline
    Task<(WorkitemDto?, WorkerActivityContext?)> GetWorkitemAsync(Guid pipelineId); // Get last known workitem for a specific pipeline
    Task<PipelineStepDto?> GetCurrentStepAsync(Guid pipelineId); // Get current step for a specific pipeline
    Task<PipelineStepDto?> LockCurrentStepAsync(Guid pipelineId); // Get and delete current step for a specific pipeline
}

public class PipelineStateService : IPipelineStateService
{
    private readonly IDatabase _redis;
    private readonly JsonSerializerOptions _jsonOptions;

    public PipelineStateService(IDatabase redis, JsonSerializerOptions jsonOptions)
    {
        _redis = redis;
        _jsonOptions = jsonOptions;
    }

    public async Task PutHeartbeatAsync(Guid pipelineId, DateTime timestamp)
    {
        var attemptNumber = await _redis.HashGetAsync("pipelines:heartbeat", pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return;
        }

        var pipelineKey = $"pipeline:{pipelineId}:{attemptNumber}";

        // set time of last heartbeat
        await _redis.StringSetAsync($"{pipelineKey}:heartbeat", timestamp.ToString("O"));
    }

    public async Task PutStepAsync(Guid pipelineId, WorkitemDto workitem, PipelineStepDto step, WorkerActivityContext activityContext)
    {
        var pipelineKey = $"pipeline:{pipelineId}:{workitem.RestoreAttempt}";

        // add pipeline to heartbeat monitoring
        await _redis.HashSetAsync("pipelines:heartbeat", pipelineId.ToString(), workitem.RestoreAttempt);

        // store workitem and step
        await _redis.StringSetAsync($"{pipelineKey}:activity", JsonSerializer.Serialize(activityContext, _jsonOptions));
        await _redis.StringSetAsync($"{pipelineKey}:step", JsonSerializer.Serialize(step, _jsonOptions));
        await _redis.StringSetAsync($"{pipelineKey}:workitem", JsonSerializer.Serialize(workitem, _jsonOptions));
    }

    public async Task DeleteStepAsync(Guid pipelineId, PipelineStepDto step)
    {
        var attemptNumber = await _redis.HashGetAsync("pipelines:heartbeat", pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return;
        }

        var pipelineKey = $"pipeline:{pipelineId}:{attemptNumber}";

        // remove pipeline from hearbeat monitoring
        await _redis.HashDeleteAsync("pipelines:heartbeat", pipelineId.ToString());
        await _redis.KeyDeleteAsync($"{pipelineKey}:heartbeat");

        // remove current step and update workitem to be able restore it
        await _redis.KeyDeleteAsync($"{pipelineKey}:activity");
        await _redis.KeyDeleteAsync($"{pipelineKey}:step");
        await _redis.KeyDeleteAsync($"{pipelineKey}:workitem");

        // so, after the final step - nothing remains of the pipeline in Redis
    }

    public async Task<Guid[]> GetHeartbeatPipelinesAsync()
    {
        var values = await _redis.HashKeysAsync("pipelines:heartbeat");
        return values
            .Where(v => v.HasValue)
            .Select(v => Guid.Parse(v.ToString()))
            .ToArray();
    }

    public async Task<string?> GetHeartbeatAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashGetAsync("pipelines:heartbeat", pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return null;
        }

        var pipelineKey = $"pipeline:{pipelineId}:{attemptNumber}";

        var heartbeat = await _redis.StringGetAsync($"{pipelineKey}:heartbeat");
        return heartbeat.HasValue ? heartbeat.ToString() : null;
    }

    public async Task<(WorkitemDto?, WorkerActivityContext?)> GetWorkitemAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashGetAsync("pipelines:heartbeat", pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return null;
        }

        var pipelineKey = $"pipeline:{pipelineId}:{attemptNumber}";

        if (!await _redis.KeyExistsAsync($"{pipelineKey}:workitem"))
        {
            return (null, null);
        }

        var workitemJson = await _redis.StringGetAsync($"{pipelineKey}:workitem");
        var activityJson = await _redis.StringGetAsync($"{pipelineKey}:activity");

        return (JsonSerializer.Deserialize<WorkitemDto>(workitemJson, _jsonOptions), 
                JsonSerializer.Deserialize<WorkerActivityContext>(activityJson, _jsonOptions));
    }

    public async Task<PipelineStepDto?> GetCurrentStepAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashGetAsync("pipelines:heartbeat", pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return null;
        }

        var pipelineKey = $"pipeline:{pipelineId}:{attemptNumber}";

        var stepDto = await _redis.StringGetAsync($"{pipelineKey}:step");
        return stepDto.HasValue ?
            JsonSerializer.Deserialize<PipelineStepDto>(stepDto, _jsonOptions) :
            null;
    }

    public async Task<PipelineStepDto?> LockCurrentStepAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashGetAsync("pipelines:heartbeat", pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return null;
        }

        var pipelineKey = $"pipeline:{pipelineId}:{attemptNumber}";

        var stepDto = await _redis.StringGetDeleteAsync($"{pipelineKey}:step");
        return stepDto.HasValue ?
            JsonSerializer.Deserialize<PipelineStepDto>(stepDto, _jsonOptions) :
            null;
    }

}