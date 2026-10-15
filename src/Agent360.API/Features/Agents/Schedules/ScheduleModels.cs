using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Schedules;

public sealed record CronScheduleDto(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("cron")] string? Cron,
    [property: JsonPropertyName("run_at")] string? RunAt,
    [property: JsonPropertyName("timezone")] string? Timezone);

public sealed record CronJobItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("schedule")] CronScheduleDto? Schedule,
    [property: JsonPropertyName("task_type")] string? TaskType,
    [property: JsonPropertyName("save_result_to_inbox")] bool SaveResultToInbox);

public sealed record ScheduleJobSummaryResponse(
    string Id,
    string Name,
    bool Enabled,
    string? CronExpression,
    string? Timezone,
    string TaskType,
    string Status);

public sealed record CreateScheduleJobRequest(
    string Name,
    string CronExpression,
    string Prompt,
    string? Timezone = "Asia/Ho_Chi_Minh");
