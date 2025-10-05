using StackExchange.Redis;
using System.Text.Json;
using shared.Models;

namespace ccp.Services;

public interface IPipelineStateService
{
    Task PutStepAsync(Guid pipelineId, WorkitemDto workitem, PipelineStepDto step);
    Task PutHeartbeatAsync(Guid pipelineId, DateTime timestamp);
    Task DeleteStepAsync(Guid pipelineId, PipelineStepDto step);

    Task<Guid[]> GetHeartbeatPipelinesAsync(); // List all heartbeat pipelines
    Task<string?> GetHeartbeatAsync(Guid pipelineId); // Get heartbeat for a specific pipeline
    Task<WorkitemDto?> GetWorkitemAsync(Guid pipelineId); // Get last known workitem for a specific pipeline
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

    public async Task PutStepAsync(Guid pipelineId, WorkitemDto workitem, PipelineStepDto step)
    {
        var pipelineKey = $"pipeline:{pipelineId}:{workitem.RestoreAttempt}";

        // add pipeline to heartbeat monitoring
        await _redis.HashSetAsync("pipelines:heartbeat", pipelineId.ToString(), workitem.RestoreAttempt);

        // store workitem and step
        await _redis.StringSetAsync($"{pipelineKey}:workitem", JsonSerializer.Serialize(workitem, _jsonOptions));
        await _redis.StringSetAsync($"{pipelineKey}:step", JsonSerializer.Serialize(step, _jsonOptions));
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

        if (step.IsFinal)
        {
            // if final step, remove workitem as well
            await _redis.KeyDeleteAsync($"{pipelineKey}:workitem");
        }

        // remove current step and update workitem to be able restore it
        await _redis.KeyDeleteAsync($"{pipelineKey}:step");
        await _redis.KeyDeleteAsync($"{pipelineKey}:heartbeat");

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

    public async Task<WorkitemDto?> GetWorkitemAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashGetAsync("pipelines:heartbeat", pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return null;
        }

        var pipelineKey = $"pipeline:{pipelineId}:{attemptNumber}";

        var workitemJson = await _redis.StringGetAsync($"{pipelineKey}:workitem");
        return workitemJson.HasValue ?
            JsonSerializer.Deserialize<WorkitemDto>(workitemJson, _jsonOptions) :
            null;
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