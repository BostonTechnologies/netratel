using FluentAssertions;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobSchedulePolicyTests
{
    [Fact]
    public void FromOptionsJson_Reads_Schedule()
    {
        var policy = JobSchedulePolicy.FromOptionsJson("""
        {"schedule":{"enabled":true,"timeZoneId":"Africa/Johannesburg","timeOfDay":"08:00","daysOfWeek":["Monday","Tuesday","Wednesday","Thursday","Friday"]}}
        """);

        policy.Should().NotBeNull();
        policy!.TimeZoneId.Should().Be("Africa/Johannesburg");
        policy.TimeOfDay.Should().Be(new TimeOnly(8, 0));
        policy.DaysOfWeek.Should().Equal(
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday);
    }

    [Fact]
    public void FromOptionsJson_Returns_Null_For_Invalid_Json()
    {
        JobSchedulePolicy.FromOptionsJson("{nope").Should().BeNull();
    }

    [Fact]
    public void MergeOptionsJson_Preserves_ExecutionPolicy()
    {
        var merged = JobSchedulePolicy.MergeOptionsJson(
            """{"executionPolicy":{"expectedRuntimeSeconds":120,"graceSeconds":30,"hardTimeoutSeconds":150}}""",
            new JobScheduleDto(
                true,
                "Africa/Johannesburg",
                "08:00",
                ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]));

        merged.Should().Contain("\"executionPolicy\"");
        merged.Should().Contain("\"schedule\"");
        JobSchedulePolicy.FromOptionsJson(merged)!.ToCronExpression().Should().Be("0 8 * * 1,2,3,4,5");
    }

    [Fact]
    public void MergeOptionsJson_Removes_Disabled_Schedule()
    {
        var merged = JobSchedulePolicy.MergeOptionsJson(
            """{"executionPolicy":{"expectedRuntimeSeconds":120},"schedule":{"enabled":true,"timeZoneId":"Africa/Johannesburg","timeOfDay":"08:00","daysOfWeek":["Monday"]}}""",
            (JobScheduleDto?)null);

        merged.Should().Contain("\"executionPolicy\"");
        merged.Should().NotContain("\"schedule\"");
    }

    [Fact]
    public void ToCronExpression_Uses_Star_For_Everyday()
    {
        var policy = JobSchedulePolicy.FromDto(new JobScheduleDto(
            true,
            "Africa/Johannesburg",
            "08:00",
            ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]));

        policy!.ToCronExpression().Should().Be("0 8 * * *");
    }

    [Fact]
    public void FromJob_Returns_Dto_Shape_For_Enabled_Schedule()
    {
        var job = new JobDefinitionInfo(
            Id: 18,
            Name: "Diagnostic",
            FolderPath: "/NetRatel/",
            Description: null,
            TenantId: 1,
            ClientIdentity: "client",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            OptionsJson: """
            {"schedule":{"enabled":true,"timeZoneId":"Africa/Johannesburg","timeOfDay":"08:00","daysOfWeek":["Monday"]}}
            """);

        JobSchedulePolicy.FromJob(job)!.ToDto().Should().BeEquivalentTo(new JobScheduleDto(
            true,
            "Africa/Johannesburg",
            "08:00",
            ["Monday"]));
    }
}
