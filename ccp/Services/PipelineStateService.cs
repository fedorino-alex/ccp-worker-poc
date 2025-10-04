using StackExchange.Redis;
using System.Text.Json;
using shared.Models;

namespace ccp.Services;

public interface IPipelineStateService
{
    Task PutStepAsync(Guid pipelineId, WorkitemDto workitem, PipelineStepDto step);
    Task HeartbeatAsync(Guid pipelineId, DateTime timestamp);
    Task DeleteStepAsync(Guid pipelineId);

    Task<Guid[]> GetPipelinesAsync(); // List all active pipelines
    Task<string?> GetHeartbeatAsync(Guid pipelineId); // Get heartbeat for a specific pipeline
    Task<WorkitemDto?> GetWorkitemAsync(Guid pipelineId); // Get last known workitem for a specific pipeline
    Task<PipelineStepDto?> GetCurrentStepAsync(Guid pipelineId); // Get current step for a specific pipeline
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

    public async Task HeartbeatAsync(Guid pipelineId, DateTime timestamp)
    {
        var pipelineKey = $"pipeline:{pipelineId}";

        // set time of last heartbeat
        await _redis.StringSetAsync($"{pipelineKey}:heartbeat", timestamp.ToString("O"));
    }

    public async Task PutStepAsync(Guid pipelineId, WorkitemDto workitem, PipelineStepDto step)
    {
        var pipelineKey = $"pipeline:{pipelineId}";

        // set current step and update workitem to be able restore it
        await _redis.StringSetAsync($"{pipelineKey}:step", JsonSerializer.Serialize(step, _jsonOptions));
        await _redis.StringSetAsync($"{pipelineKey}:workitem", JsonSerializer.Serialize(workitem, _jsonOptions));
    }

    public async Task DeleteStepAsync(Guid pipelineId)
    {
        var pipelineKey = $"pipeline:{pipelineId}";

        // remove current step and update workitem to be able restore it
        await _redis.KeyDeleteAsync($"{pipelineKey}:step");
        await _redis.KeyDeleteAsync($"{pipelineKey}:heartbeat");
    }

    public async Task<Guid[]> GetPipelinesAsync()
    {
        var values = await _redis.SetMembersAsync("pipelines:all");
        return values
            .Where(v => v.HasValue)
            .Select(v => Guid.Parse(v.ToString()))
            .ToArray();
    }

    public async Task<string?> GetHeartbeatAsync(Guid pipelineId)
    {
        var pipelineKey = $"pipeline:{pipelineId}";

        var heartbeat = await _redis.StringGetAsync($"{pipelineKey}:heartbeat");
        return heartbeat.HasValue ? heartbeat.ToString() : null;
    }

    public async Task<WorkitemDto?> GetWorkitemAsync(Guid pipelineId)
    {
        var pipelineKey = $"pipeline:{pipelineId}";

        var workitemJson = await _redis.StringGetAsync($"{pipelineKey}:workitem");
        return workitemJson.HasValue ?
            JsonSerializer.Deserialize<WorkitemDto>(workitemJson, _jsonOptions) :
            null;
    }

    public async Task<PipelineStepDto?> GetCurrentStepAsync(Guid pipelineId)
    {
        var pipelineKey = $"pipeline:{pipelineId}";

        var stepDto = await _redis.StringGetAsync($"{pipelineKey}:step");
        return stepDto.HasValue ?
            JsonSerializer.Deserialize<PipelineStepDto>(stepDto, _jsonOptions) :
            null;
    }
}