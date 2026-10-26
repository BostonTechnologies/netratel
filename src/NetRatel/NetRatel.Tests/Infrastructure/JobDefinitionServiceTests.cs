using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobDefinitionServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task In_memory_reference_validation_rejects_missing_and_deleted_sources(bool update, bool deleted)
    {
        await using var db = CreateDb();
        var service = new JobDefinitionService(db);
        var job = await service.CreateAsync(new("reference-validation", "/", null, null, string.Empty));
        var step = await service.AddStepAsync(new(job.Id, 1, JobStepKind.RunCommand, "sh", "true", null, null, true));
        const ulong sourceId = 912;
        if (deleted)
        {
            db.Scripts.Add(new ScriptDefinition { Id = (long)sourceId, Name = "deleted", Content = "true",
                DeletedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Func<Task> write = update
            ? async () => await service.UpdateStepAsync(new(step!.Id, JobStepKind.LibraryScript, null, null, sourceId, null, null))
            : async () => await service.AddStepAsync(new(job.Id, 1, JobStepKind.LibraryScript, null, null, sourceId, null, true));
        await write.Should().ThrowAsync<ArgumentException>();
        (await db.JobSteps.SingleAsync()).ScriptId.Should().BeNull();
        (await db.JobSteps.SingleAsync()).Ordinal.Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_Retires_Definition_And_Preserves_Job_Run_History()
    {
        await using var db = CreateDb();
        var service = new JobDefinitionService(db);

        var job = await service.CreateAsync(new CreateJobDefinitionCommand(
            "Legacy test job",
            "/ExampleOrganization/",
            "job with historical runs",
            TenantId: 1,
            ClientIdentity: "C20017C3B2C49B2F3B846CB35979EE8D4501FEE0923AE1FD47C4D393670B0350"));

        var step = await service.AddStepAsync(new AddJobStepCommand(
            job.Id,
            Ordinal: null,
            Type: JobStepKind.RunCommand,
            Runner: "cmd",
            Command: "whoami",
            ScriptId: null,
            PayloadJson: null,
            Enabled: true));
        var parameter = await service.AddParamAsync(new(job.Id, "fixture", "string", false, "retained", null, null));

        db.JobRuns.Add(new JobRunRecord
        {
            Id = 4100,
            JobId = (long)job.Id,
            TenantId = 1,
            ClientIdentity = job.ClientIdentity,
            StartedBy = "test",
            Status = (int)JobRunState.Succeeded,
            CurrentStepOrdinal = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Steps =
            [
                new JobStepRunRecord
                {
                    Id = 5100,
                    JobStepId = (long)step!.Id,
                    Status = (int)JobStepRunState.Succeeded,
                    Ordinal = 1,
                    TaskRequestId = "req-4100",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                }
            ],
            Activities =
            [
                new JobTaskActivityRecord
                {
                    Id = 6100,
                    RequestId = "req-4100",
                    JobStepId = (long)step.Id,
                    ClientIdentity = job.ClientIdentity,
                    TenantId = 1,
                    TaskType = "exec-shell-cmd",
                    Status = "Completed",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Logs =
                    [
                        new JobTaskLogRecord
                        {
                            RequestId = "req-4100",
                            ClientIdentity = job.ClientIdentity,
                            Stream = "stdout",
                            Message = "done",
                            Sequence = 1,
                            TimestampUtc = DateTimeOffset.UtcNow
                        }
                    ]
                }
            ]
        });
        db.JobTaskActivities.Add(new JobTaskActivityRecord
        {
            Id = 6200,
            RequestId = "ad-hoc-step-activity",
            JobRunId = null,
            JobStepId = (long)step.Id,
            ClientIdentity = job.ClientIdentity,
            TenantId = 1,
            TaskType = "exec-shell-cmd",
            Status = "Completed",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Logs =
            [
                new JobTaskLogRecord
                {
                    RequestId = "ad-hoc-step-activity",
                    ClientIdentity = job.ClientIdentity,
                    Stream = "stdout",
                    Message = "ad hoc done",
                    Sequence = 1,
                    TimestampUtc = DateTimeOffset.UtcNow
                }
            ]
        });
        await db.SaveChangesAsync();

        var deleted = await service.DeleteAsync(job.Id);

        deleted.Should().NotBeNull();
        (await db.Jobs.CountAsync()).Should().Be(0);
        var retained = await db.Jobs.IgnoreQueryFilters().SingleAsync();
        retained.Id.Should().Be((long)job.Id);
        retained.Name.Should().Be(job.Name);
        retained.DeletedAtUtc.Should().NotBeNull();
        retained.UpdatedAtUtc.Should().Be(retained.DeletedAtUtc!.Value);
        (await service.GetAsync(job.Id)).Should().BeNull();
        (await service.GetDetailsAsync(job.Id)).Should().BeNull();
        (await service.ListAsync()).Should().BeEmpty();
        (await service.DeleteAsync(job.Id)).Should().BeNull();
        (await service.ListParamsAsync(job.Id)).Should().BeEmpty();
        (await service.ListStepsAsync(job.Id)).Should().BeEmpty();
        (await service.AddParamAsync(new(job.Id, "new", "string", false, null, null, null))).Should().BeNull();
        (await service.UpdateParamAsync(new(parameter!.Id, "changed", null, null, null, null, null))).Should().BeNull();
        (await service.DeleteParamAsync(parameter.Id)).Should().BeFalse();
        (await service.AddStepAsync(new(job.Id, 2, JobStepKind.RunCommand, "sh", "changed", null, null, true))).Should().BeNull();
        (await service.UpdateStepAsync(new(step.Id, null, null, "changed", null, null, null))).Should().BeNull();
        (await service.ReorderStepAsync(step.Id, 2)).Should().BeNull();
        (await service.DeleteStepAsync(step.Id)).Should().BeFalse();
        (await db.JobParameters.SingleAsync()).DefaultValue.Should().Be("retained");
        (await db.JobSteps.SingleAsync()).Command.Should().Be("whoami");
        (await db.JobRuns.SingleAsync()).Id.Should().Be(4100);
        (await db.JobStepRuns.SingleAsync()).Id.Should().Be(5100);
        (await db.JobTaskActivities.Select(x => x.Id).ToArrayAsync()).Should().BeEquivalentTo([6100L, 6200L]);
        (await db.JobTaskLogs.Select(x => x.Message).ToArrayAsync()).Should().BeEquivalentTo(["done", "ad hoc done"]);

        var runs = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        (await runs.GetDetailsAsync(4100))!.Steps.Should().ContainSingle(x => x.Id == 5100);
        Func<Task> startRetired = () => runs.UpsertRunAsync(new(4101, job.Id, 1, job.ClientIdentity,
            "test", JobRunState.Pending, 0, DateTimeOffset.UtcNow, null, null, null, null, null));
        await startRetired.Should().ThrowAsync<JobDefinitionUnavailableException>();
        (await db.JobRuns.CountAsync()).Should().Be(1);
    }

    private static OrchestratorDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new OrchestratorDbContext(options);
    }
}
