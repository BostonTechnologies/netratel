using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobTargetScheduleValidationTests
{
    private const string EnabledSchedule = "{\"schedule\":{\"enabled\":true,\"timeZoneId\":\"UTC\",\"timeOfDay\":\"08:00\",\"daysOfWeek\":[\"Monday\"]}}";

    [Fact]
    public async Task Incomplete_runtime_schedule_cannot_be_created_or_activated_or_left_enabled_after_target_edit()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var jobs = new JobDefinitionService(db);
        var runtime = new JobTargetDefinition { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.ClientId, "ClientId") };
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => jobs.CreateAsync(new("runtime", "/", null, 7, "", EnabledSchedule) { Target = runtime })))
            .Code.Should().Be("target_schedule_inputs_required");
        var job = await jobs.CreateAsync(new("runtime", "/", null, 7, "") { Target = runtime });
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => jobs.UpdateAsync(new(job.Id, null, null, null, 7, null, EnabledSchedule))))
            .Code.Should().Be("target_schedule_inputs_required");
        var withDefault = runtime with { RuntimeInput = runtime.RuntimeInput! with { DefaultValues = [Guid.NewGuid().ToString("D")] } };
        var scheduled = await jobs.UpdateAsync(new(job.Id, null, null, null, 7, null, EnabledSchedule) { Target = withDefault });
        scheduled!.OptionsJson.Should().Contain("schedule");
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => jobs.UpdateAsync(new(job.Id, null, null, null, 7, null) { Target = runtime })))
            .Code.Should().Be("target_schedule_inputs_required");
    }

    [Fact]
    public async Task Parameterized_schedules_require_declared_defaults_and_referenced_parameter_edits_are_atomic()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var jobs = new JobDefinitionService(db);
        var target = new JobTargetDefinition { Source = JobTargetSource.NodeFilter, Filter = "tags: @option.group@" };
        var job = await jobs.CreateAsync(new("filtered", "/", null, 7, "") { Target = target });
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => jobs.UpdateAsync(new(job.Id, null, null, null, 7, null, EnabledSchedule))))
            .Code.Should().Be("target_schedule_inputs_required");
        var parameter = (await jobs.AddParamAsync(new(job.Id, "group", "string", true, "TestSystem", null, null)))!;
        await jobs.UpdateAsync(new(job.Id, null, null, null, 7, null, EnabledSchedule));
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => jobs.DeleteParamAsync(parameter.Id))).Code.Should().Be("target_schedule_inputs_required");
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => jobs.UpdateParamAsync(new(parameter.Id, "renamed", null, null, null, null, null))))
            .Code.Should().Be("target_schedule_inputs_required");
        (await jobs.ListParamsAsync(job.Id)).Should().ContainSingle(x => x.Name == "group" && x.DefaultValue == "TestSystem");
    }
}
