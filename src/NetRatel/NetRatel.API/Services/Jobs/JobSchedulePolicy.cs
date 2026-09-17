using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

public sealed record JobSchedulePolicy(
    bool Enabled,
    string TimeZoneId,
    TimeOnly TimeOfDay,
    IReadOnlyList<DayOfWeek> DaysOfWeek)
{
    public const string DefaultTimeZoneId = "Africa/Johannesburg";
    public const string OptionsPropertyName = "schedule";

    private static readonly DayOfWeek[] AllDays =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    ];

    public JobScheduleDto ToDto()
        => new(
            Enabled,
            TimeZoneId,
            TimeOfDay.ToString("HH:mm"),
            DaysOfWeek.Select(day => day.ToString()).ToList());

    public string ToCronExpression()
    {
        var minute = TimeOfDay.Minute;
        var hour = TimeOfDay.Hour;
        var days = DaysOfWeek.Count == 7
            ? "*"
            : string.Join(",", DaysOfWeek.Select(ToCronDayNumber).Distinct().Order());

        return $"{minute} {hour} * * {days}";
    }

    public TimeZoneInfo ResolveTimeZone()
        => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public static JobSchedulePolicy? FromJob(JobDefinitionInfo job)
        => FromOptionsJson(job.OptionsJson);

    public static JobSchedulePolicy? FromDto(JobScheduleDto? dto)
    {
        if (dto is null || !dto.Enabled)
        {
            return null;
        }

        var timeZoneId = string.IsNullOrWhiteSpace(dto.TimeZoneId)
            ? DefaultTimeZoneId
            : dto.TimeZoneId.Trim();

        if (!IsValidTimeZone(timeZoneId))
        {
            return null;
        }

        if (!TimeOnly.TryParse(dto.TimeOfDay, out var timeOfDay))
        {
            return null;
        }

        var days = NormalizeDays(dto.DaysOfWeek);
        return days.Count == 0
            ? null
            : new JobSchedulePolicy(true, timeZoneId, timeOfDay, days);
    }

    public static JobSchedulePolicy? FromOptionsJson(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (!doc.RootElement.TryGetProperty(OptionsPropertyName, out var schedule) ||
                schedule.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var enabled = !schedule.TryGetProperty("enabled", out var enabledNode) ||
                (enabledNode.ValueKind == JsonValueKind.True) ||
                (enabledNode.ValueKind == JsonValueKind.String &&
                    bool.TryParse(enabledNode.GetString(), out var enabledText) &&
                    enabledText);

            if (!enabled)
            {
                return null;
            }

            var timeZoneId = ReadString(schedule, "timeZoneId") ?? DefaultTimeZoneId;
            if (!IsValidTimeZone(timeZoneId))
            {
                return null;
            }

            var timeText = ReadString(schedule, "timeOfDay") ?? "08:00";
            if (!TimeOnly.TryParse(timeText, out var timeOfDay))
            {
                return null;
            }

            var days = schedule.TryGetProperty("daysOfWeek", out var daysNode) && daysNode.ValueKind == JsonValueKind.Array
                ? NormalizeDays(daysNode.EnumerateArray().Select(ReadDayString))
                : AllDays.ToList();

            return days.Count == 0
                ? null
                : new JobSchedulePolicy(true, timeZoneId, timeOfDay, days);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string MergeOptionsJson(string? optionsJson, JobScheduleDto? schedule)
        => MergeOptionsJson(optionsJson, FromDto(schedule));

    public static string MergeOptionsJson(string? optionsJson, JobSchedulePolicy? schedule)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(optionsJson)
                ? new JsonObject()
                : JsonNode.Parse(optionsJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        if (schedule is null)
        {
            root.Remove(OptionsPropertyName);
        }
        else
        {
            root[OptionsPropertyName] = new JsonObject
            {
                ["enabled"] = schedule.Enabled,
                ["timeZoneId"] = schedule.TimeZoneId,
                ["timeOfDay"] = schedule.TimeOfDay.ToString("HH:mm"),
                ["daysOfWeek"] = new JsonArray(schedule.DaysOfWeek.Select(day => JsonValue.Create(day.ToString())).ToArray<JsonNode?>())
            };
        }

        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static List<DayOfWeek> NormalizeDays(IEnumerable<string?>? dayNames)
    {
        if (dayNames is null)
        {
            return [];
        }

        return dayNames
            .Select(day => Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var parsed) ? parsed : (DayOfWeek?)null)
            .Where(day => day.HasValue)
            .Select(day => day!.Value)
            .Distinct()
            .OrderBy(day => day == DayOfWeek.Sunday ? 7 : (int)day)
            .ToList();
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static string? ReadDayString(JsonElement node)
        => node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static int ToCronDayNumber(DayOfWeek day)
        => day == DayOfWeek.Sunday ? 0 : (int)day;

    private static bool IsValidTimeZone(string timeZoneId)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
