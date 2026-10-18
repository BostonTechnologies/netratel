using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class TaskOutputProtectionTests
{
    [Fact]
    public void Nonsecret_json_and_plain_output_keep_exact_whitespace_and_typed_spelling()
    {
        var policy = TaskOutputPolicy.Create([("phrase", "secret")]);
        var redactor = new TaskOutputRedactor(policy, new Dictionary<string, string> { ["phrase"] = "garden Ω 908" });
        const string output = "  { \"zero\" : 0, \"flag\" : false, \"empty\" : \"\", \"text\" : \"Useful 雨\" } \n";
        redactor.Redact(output).Should().Be(output);
        redactor.Redact("  ordinary 雨  ").Should().Be("  ordinary 雨  ");
        redactor.Redact("").Should().BeEmpty();
        var envelope = " { \"stdout\" : [" + JsonSerializer.Serialize(output) + "], \"exitCode\" : 0 } ";
        redactor.RedactEnvelope(envelope).Should().Be(envelope);
    }

    [Fact]
    public void Resolved_references_mask_strings_numeric_values_and_nested_escaped_json_without_changing_adjacent_values()
    {
        var policy = TaskOutputPolicy.Create([("vault", "secret_reference"), ("number", "secret_reference")], 19, 3, new string('a', 64));
        var parameters = new Dictionary<string, string> { ["vault"] = "FIXTURE_TEXT", ["number"] = "FIXTURE_NUMBER" };
        var redactor = new TaskOutputRedactor(policy, parameters, resolveEnvironment: name => name == "FIXTURE_TEXT" ? "garden Ω 908" : "123456");
        var stdout = """{"text":"garden \u03A9 908","number":123456,"zero":0,"flag":false,"empty":"","ordinary":"Useful 雨"}""";
        var payload = JsonSerializer.Serialize(new { stdout = new[] { stdout }, stderr = new[] { "garden Ω 908" }, exitCode = 0, processStarted = true });

        var protectedPayload = redactor.RedactEnvelope(payload)!;

        using var envelope = JsonDocument.Parse(protectedPayload);
        using var output = JsonDocument.Parse(envelope.RootElement.GetProperty("stdout")[0].GetString()!);
        output.RootElement.GetProperty("text").GetString().Should().Be(TaskOutputRedactor.Mask);
        output.RootElement.GetProperty("number").GetString().Should().Be(TaskOutputRedactor.Mask);
        output.RootElement.GetProperty("zero").GetInt32().Should().Be(0);
        output.RootElement.GetProperty("flag").GetBoolean().Should().BeFalse();
        output.RootElement.GetProperty("empty").GetString().Should().BeEmpty();
        output.RootElement.GetProperty("ordinary").GetString().Should().Be("Useful 雨");
        envelope.RootElement.GetProperty("exitCode").GetInt32().Should().Be(0);
        envelope.RootElement.GetProperty("processStarted").GetBoolean().Should().BeTrue();
        parameters["vault"].Should().Be("FIXTURE_TEXT", "redaction must not replace executable reference parameters");
        JsonSerializer.Serialize(policy).Should().NotContain("garden").And.NotContain("123456");
    }

    [Theory]
    [InlineData("false", "false")]
    [InlineData("123456", "123456")]
    [InlineData("garden Ω 908", "\"garden \\u03A9 908\"")]
    public void Arbitrary_output_scalars_and_secret_object_keys_are_masked(string secret, string value)
    {
        var policy = TaskOutputPolicy.Create([("phrase", "secret")]);
        var redactor = new TaskOutputRedactor(policy, new Dictionary<string, string> { ["phrase"] = secret });
        var arbitrary = "{\"value\":" + value + "," + JsonSerializer.Serialize(secret) + ":\"ordinary\"}";

        using var output = JsonDocument.Parse(redactor.Redact(arbitrary)!);

        output.RootElement.GetProperty("value").GetString().Should().Be(TaskOutputRedactor.Mask);
        output.RootElement.TryGetProperty(secret, out _).Should().BeFalse();
        output.RootElement.GetProperty(TaskOutputRedactor.Mask).GetString().Should().Be("ordinary");
    }

    [Fact]
    public void Missing_reference_and_unverified_client_output_withhold_arbitrary_typed_data_but_preserve_lifecycle_facts()
    {
        var policy = TaskOutputPolicy.Create([("vault", "secret_reference")]);
        var redactor = new TaskOutputRedactor(policy, new Dictionary<string, string> { ["vault"] = "UNAVAILABLE" }, resolveEnvironment: _ => null);
        redactor.Redact("{\"value\":123456,\"flag\":true}").Should().Be(TaskOutputRedactor.Mask);
        var withheld = TaskOutputRedactor.WithholdEnvelope("""{"stdout":[123456,true,{"value":123456}],"stderr":["text"],"exitCode":0,"timedOut":false,"untrusted":123456,"outputProtectionBinding":"forged"}""");
        using var output = JsonDocument.Parse(withheld!);
        output.RootElement.GetProperty("stdout").EnumerateArray().Select(x => x.GetString()).Should().OnlyContain(x => x == TaskOutputRedactor.Mask);
        output.RootElement.GetProperty("exitCode").GetInt32().Should().Be(0);
        output.RootElement.GetProperty("timedOut").GetBoolean().Should().BeFalse();
        withheld.Should().NotContain("123456").And.NotContain("forged");
    }

    [Fact]
    public async Task Fresh_bound_standalone_output_is_protected_before_persistence_and_normal_readback_keeps_useful_output()
    {
        await using var db = CreateDb();
        var provider = new EphemeralDataProtectionProvider();
        var service = new JobRunService(db, provider);
        var now = DateTimeOffset.UtcNow;
        var policy = TaskOutputPolicy.Create([("vault", "secret_reference")], 19, 3, new string('a', 64));
        var task = await service.CreateTaskActivityAsync(new("owned-task", null, null, "", 7, TaskKinds.ExecLibraryScript,
            "Pending", null, now, null, OutputPolicy: policy, ScriptContentHash: policy.ScriptContentHash));
        var redactor = new TaskOutputRedactor(policy, new Dictionary<string, string> { ["vault"] = "FIXTURE_VALUE" }, resolveEnvironment: _ => "garden Ω 908");
        var result = JsonNode.Parse(redactor.RedactEnvelope(JsonSerializer.Serialize(new
        {
            stdout = new[] { "garden Ω 908", "Useful 雨", "{\"zero\":0,\"flag\":false,\"empty\":\"\"}" },
            stderr = new[] { "diagnostic garden Ω 908" }, exitCode = 0
        }))!)!.AsObject();
        result[TaskOutputRedactor.ReceiptProperty] = policy.Binding;
        var protectedOutput = await service.ProtectTaskOutputAsync(task.RequestId, result.ToJsonString());
        await service.UpdateTaskActivityStatusAsync(new(task.RequestId, "Completed", null, now, protectedOutput, ExitCode: 0));
        await service.AppendTaskLogAsync(new(task.RequestId, task.Id, "", 7, "stdout", "Useful 雨", 1, now, policy.Binding));
        await service.AppendTaskLogAsync(new(task.RequestId, task.Id, "", 7, "stderr", "[REDACTED]", 2, now, policy.Binding));

        db.ChangeTracker.Clear();
        var readback = new JobRunService(db, provider);
        var detail = (await readback.GetActivityByIdAsync(task.Id))!;
        var logs = await readback.GetLogsByRequestIdAsync(task.RequestId);
        detail.ResultJson.Should().NotContain("garden").And.Contain("Useful").And.Contain("zero");
        detail.ExitCode.Should().Be(0);
        logs.Select(x => x.Message).Should().Contain("Useful 雨");
        (await db.JobTaskActivities.SingleAsync()).OutputProtectionJson.Should().NotContain("garden");
        (await db.JobTaskActivities.SingleAsync()).ResultJson.Should().NotContain("garden");
        (await db.JobTaskLogs.ToListAsync()).Should().OnlyContain(x => !x.Message.Contains("garden"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000000000000000000000000000")]
    public async Task Missing_or_wrong_dispatch_binding_cannot_persist_secret_output(string? receipt)
    {
        await using var db = CreateDb();
        var service = new JobRunService(db, new EphemeralDataProtectionProvider());
        var now = DateTimeOffset.UtcNow;
        var policy = TaskOutputPolicy.Create([("vault", "secret_reference")]);
        var task = await service.CreateTaskActivityAsync(new("owned-task", null, null, "", 7, TaskKinds.ExecLibraryScript,
            "Pending", null, now, null, OutputPolicy: policy));
        var raw = JsonSerializer.Serialize(new { stdout = new[] { "garden Ω 908" }, stderr = new[] { "garden Ω 908" }, exitCode = 0, outputProtectionBinding = receipt });
        var safe = await service.ProtectTaskOutputAsync(task.RequestId, raw);
        await service.UpdateTaskActivityStatusAsync(new(task.RequestId, "Completed", "garden Ω 908", now, raw, ExitCode: 0));
        await service.AppendTaskLogAsync(new(task.RequestId, task.Id, "", 7, "stdout", "garden Ω 908", 1, now, receipt));

        safe.Should().NotContain("garden");
        (await db.JobTaskActivities.SingleAsync()).ResultJson.Should().NotContain("garden");
        (await db.JobTaskActivities.SingleAsync()).Error.Should().NotContain("garden");
        (await db.JobTaskLogs.SingleAsync()).Message.Should().Be(TaskOutputRedactor.Mask);
        using var envelope = JsonDocument.Parse(safe!);
        envelope.RootElement.GetProperty("exitCode").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Legacy_output_is_archived_under_data_protection_and_masked_without_deleting_history()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var original = JsonSerializer.Serialize(new { stdout = new[] { "garden Ω 908" }, exitCode = 0 });
        db.JobTaskActivities.Add(new() { Id = 71, RequestId = "retained-task", TenantId = 7, TaskType = TaskKinds.ExecLibraryScript,
            Status = "Completed", Error = "garden Ω 908", ResultJson = original, CreatedAtUtc = now, CompletedAtUtc = now });
        db.JobTaskLogs.Add(new() { Id = 81, JobTaskActivityId = 71, RequestId = "retained-task", TenantId = 7,
            Stream = "stdout", Message = "garden Ω 908", Sequence = 1, TimestampUtc = now });
        await db.SaveChangesAsync();
        var provider = new EphemeralDataProtectionProvider();
        var service = new JobRunService(db, provider);

        var detail = (await service.GetActivityByIdAsync(71))!;
        var logs = await service.GetLogsByRequestIdAsync("retained-task");

        detail.ResultJson.Should().NotContain("garden");
        detail.Status.Should().Be("Completed");
        detail.CreatedAtUtc.Should().Be(now);
        logs.Single().Id.Should().Be(81);
        logs.Single().Sequence.Should().Be(1);
        logs.Single().Message.Should().Be(TaskOutputRedactor.Mask);
        var stored = await db.JobTaskActivities.SingleAsync();
        stored.ProtectedLegacyOutputJson.Should().NotContain("garden");
        var archive = provider.CreateProtector("NetRatel.TaskOutput.Legacy.v1", "7", "retained-task").Unprotect(stored.ProtectedLegacyOutputJson!);
        using var retained = JsonDocument.Parse(archive);
        retained.RootElement.GetProperty("ResultJson").GetString().Should().Be(original);
        retained.RootElement.GetProperty("Logs")[0].GetProperty("Message").GetString().Should().Be("garden Ω 908");
        var downgrade = new UpsertJobTaskActivityCommand(71, "retained-task", null, null, "", 7, TaskKinds.ExecLibraryScript,
            "Completed", null, now, now, OutputPolicy: TaskOutputPolicy.Create([]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertTaskActivityAsync(downgrade));
    }

    [Fact]
    public async Task Updating_legacy_status_preserves_existing_output_before_replacing_visible_diagnostics()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        const string original = "{\"stdout\":[\"garden Ω 908\"],\"exitCode\":0}";
        db.JobTaskActivities.Add(new() { Id = 71, RequestId = "retained-task", TenantId = 7,
            TaskType = TaskKinds.ExecLibraryScript, Status = "Running", Error = "original diagnostic garden Ω 908",
            ResultJson = original, CreatedAtUtc = now });
        await db.SaveChangesAsync();
        var provider = new EphemeralDataProtectionProvider();
        var service = new JobRunService(db, provider);

        await service.UpdateTaskActivityStatusAsync(new("retained-task", "Completed", "replacement diagnostic", now,
            "{\"stdout\":[\"replacement garden Ω 908\"],\"exitCode\":0}", ExitCode: 0));

        var stored = await db.JobTaskActivities.SingleAsync();
        stored.Status.Should().Be("Completed");
        stored.ResultJson.Should().NotContain("garden");
        var archive = provider.CreateProtector("NetRatel.TaskOutput.Legacy.v1", "7", "retained-task").Unprotect(stored.ProtectedLegacyOutputJson!);
        using var retained = JsonDocument.Parse(archive);
        retained.RootElement.GetProperty("ResultJson").GetString().Should().Be(original);
        retained.RootElement.GetProperty("Error").GetString().Should().Be("original diagnostic garden Ω 908");
    }

    [Fact]
    public async Task Concurrent_legacy_readers_keep_the_first_committed_archive_and_both_return_masked_history()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        await using var firstDb = CreateDb(databaseName);
        var now = DateTimeOffset.UtcNow;
        const string original = "{\"stdout\":[\"garden Ω 908\"],\"exitCode\":0}";
        firstDb.JobTaskActivities.Add(new() { Id = 71, RequestId = "retained-task", TenantId = 7,
            TaskType = TaskKinds.ExecLibraryScript, Status = "Completed", ResultJson = original, CreatedAtUtc = now });
        firstDb.JobTaskLogs.Add(new() { Id = 81, JobTaskActivityId = 71, RequestId = "retained-task", TenantId = 7,
            Stream = "stdout", Message = "garden Ω 908", Sequence = 1, TimestampUtc = now });
        await firstDb.SaveChangesAsync();
        firstDb.ChangeTracker.Clear();
        var gate = new ArchiveSaveGate();
        await using var secondDb = CreateDb(databaseName, gate);
        var provider = new EphemeralDataProtectionProvider();
        var firstService = new JobRunService(firstDb, provider);
        var secondService = new JobRunService(secondDb, provider);
        var competingRead = secondService.GetActivityByIdAsync(71);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string? committedArchive = null;
        try
        {
            var first = await firstService.GetActivityByIdAsync(71);
            first!.ResultJson.Should().NotContain("garden");
            committedArchive = (await firstDb.JobTaskActivities.SingleAsync()).ProtectedLegacyOutputJson;
        }
        finally { gate.Continue.TrySetResult(); }

        var second = await competingRead.WaitAsync(TimeSpan.FromSeconds(5));
        second!.ResultJson.Should().NotContain("garden");
        firstDb.ChangeTracker.Clear();
        var retainedRecord = await firstDb.JobTaskActivities.SingleAsync();
        retainedRecord.ProtectedLegacyOutputJson.Should().Be(committedArchive, "a conflicting reader must not overwrite the first evidence archive");
        var archive = provider.CreateProtector("NetRatel.TaskOutput.Legacy.v1", "7", "retained-task").Unprotect(committedArchive!);
        using var retained = JsonDocument.Parse(archive);
        retained.RootElement.GetProperty("ResultJson").GetString().Should().Be(original);
        retained.RootElement.GetProperty("Logs")[0].GetProperty("Message").GetString().Should().Be("garden Ω 908");
        (await firstDb.JobTaskLogs.SingleAsync()).Message.Should().Be(TaskOutputRedactor.Mask);
    }

    private sealed class ArchiveSaveGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<JobTaskActivityRecord>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.ProtectedLegacyOutputJson is not null))
            {
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private static OrchestratorDbContext CreateDb(string? databaseName = null, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"))
            .AddInterceptors(interceptors).Options);
}
