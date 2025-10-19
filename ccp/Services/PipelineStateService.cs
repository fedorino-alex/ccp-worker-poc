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

    // Redis key constants
    private const string HeartbeatHashKey = "pipelines:heartbeat";
    private const string PipelineKeyTemplate = "pipeline:{0}:{1}"; // pipelineId:attemptNumber
    private const string ActivitySuffix = ":activity";
    private const string StepSuffix = ":step";
    private const string WorkitemSuffix = ":workitem";
    private const string HeartbeatSuffix = ":heartbeat";

    public PipelineStateService(IDatabase redis, JsonSerializerOptions jsonOptions)
    {
        _redis = redis;
        _jsonOptions = jsonOptions;
    }

    private static string GetPipelineKey(Guid pipelineId, RedisValue attemptNumber) =>
        string.Format(PipelineKeyTemplate, pipelineId, attemptNumber);

    public async Task PutHeartbeatAsync(Guid pipelineId, DateTime timestamp)
    {
        var attemptNumber = await _redis.HashGetAsync(HeartbeatHashKey, pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return;
        }

        var pipelineKey = GetPipelineKey(pipelineId, attemptNumber);

        // set time of last heartbeat
        await _redis.StringSetAsync(pipelineKey + HeartbeatSuffix, timestamp.ToString("O"));
    }

    public async Task PutStepAsync(Guid pipelineId, WorkitemDto workitem, PipelineStepDto step, WorkerActivityContext activityContext)
    {
        var pipelineKey = GetPipelineKey(pipelineId, workitem.RestoreAttempt);

        // add pipeline to heartbeat monitoring
        await _redis.HashSetAsync(HeartbeatHashKey, pipelineId.ToString(), workitem.RestoreAttempt);

        // store workitem and step
        await _redis.StringSetAsync(pipelineKey + ActivitySuffix, JsonSerializer.Serialize(activityContext, _jsonOptions));
        await _redis.StringSetAsync(pipelineKey + StepSuffix, JsonSerializer.Serialize(step, _jsonOptions));
        await _redis.StringSetAsync(pipelineKey + WorkitemSuffix, JsonSerializer.Serialize(workitem, _jsonOptions));
    }

    public async Task DeleteStepAsync(Guid pipelineId, PipelineStepDto step)
    {
        var attemptNumber = await _redis.HashFieldGetAndDeleteAsync(HeartbeatHashKey, pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return;
        }

        var pipelineKey = GetPipelineKey(pipelineId, attemptNumber);

        // remove current step and update workitem to be able restore it
        await _redis.KeyDeleteAsync(pipelineKey + ActivitySuffix);
        await _redis.KeyDeleteAsync(pipelineKey + HeartbeatSuffix);
        await _redis.KeyDeleteAsync(pipelineKey + StepSuffix);
        await _redis.KeyDeleteAsync(pipelineKey + WorkitemSuffix);

        // so, after the final step - nothing remains of the pipeline in Redis
    }

    public async Task<Guid[]> GetHeartbeatPipelinesAsync()
    {
        var values = await _redis.HashKeysAsync(HeartbeatHashKey);
        return values
            .Where(v => v.HasValue)
            .Select(v => Guid.Parse(v.ToString()))
            .ToArray();
    }

    public async Task<string?> GetHeartbeatAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashGetAsync(HeartbeatHashKey, pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return null;
        }

        var pipelineKey = GetPipelineKey(pipelineId, attemptNumber);

        var heartbeat = await _redis.StringGetAsync(pipelineKey + HeartbeatSuffix);
        return heartbeat.HasValue ? heartbeat.ToString() : null;
    }

    public async Task<(WorkitemDto?, WorkerActivityContext?)> GetWorkitemAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashGetAsync(HeartbeatHashKey, pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return (null, null);
        }

        var pipelineKey = GetPipelineKey(pipelineId, attemptNumber);

        if (!await _redis.KeyExistsAsync(pipelineKey + WorkitemSuffix))
        {
            return (null, null);
        }

        var workitemJson = await _redis.StringGetAsync(pipelineKey + WorkitemSuffix);
        var activityJson = await _redis.StringGetAsync(pipelineKey + ActivitySuffix);

        return (JsonSerializer.Deserialize<WorkitemDto>(workitemJson!, _jsonOptions), 
                JsonSerializer.Deserialize<WorkerActivityContext>(activityJson!, _jsonOptions));
    }

    public async Task<PipelineStepDto?> GetCurrentStepAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashGetAsync(HeartbeatHashKey, pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return null;
        }

        var pipelineKey = GetPipelineKey(pipelineId, attemptNumber);

        var stepDto = await _redis.StringGetAsync(pipelineKey + StepSuffix);
        return stepDto.HasValue ?
            JsonSerializer.Deserialize<PipelineStepDto>(stepDto!, _jsonOptions) :
            null;
    }

    public async Task<PipelineStepDto?> LockCurrentStepAsync(Guid pipelineId)
    {
        var attemptNumber = await _redis.HashFieldGetAndDeleteAsync(HeartbeatHashKey, pipelineId.ToString());
        if (attemptNumber.IsNull)
        {
            // no such pipeline in heartbeat monitoring
            return null;
        }

        var pipelineKey = GetPipelineKey(pipelineId, attemptNumber);

        var stepDto = await _redis.StringGetDeleteAsync(pipelineKey + StepSuffix);
        return stepDto.HasValue ?
            JsonSerializer.Deserialize<PipelineStepDto>(stepDto.ToString(), _jsonOptions) :
            null;
    }

}