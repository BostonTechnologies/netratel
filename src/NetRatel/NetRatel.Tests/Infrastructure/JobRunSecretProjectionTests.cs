using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobRunSecretProjectionTests
{
    [Fact]
    public async Task Submitted_secret_is_absent_from_ordinary_reads_and_both_option_paths()
    {
        await using var db = CreateDb();
        db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Owned inert fixture" });
        db.JobParameters.Add(new() { Id = 42, JobId = 41, Name = "phrase", Type = "secret" });
        await db.SaveChangesAsync();
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        const string sentinel = "garden rain Ω 908";
        const string inputs = "{\"phrase\":\"garden rain Ω 908\",\"empty\":\"\",\"zero\":0,\"flag\":false,\"text\":\"Useful 雨\"}";
        var submitted = await service.UpsertRunAsync(new(51, 41, 7, "", "fixture", JobRunState.Pending,
            0, DateTimeOffset.UtcNow, null, null, null, inputs,
            "[{\"name\":\"phrase\",\"value\":\"garden rain Ω 908\"}]"));
        foreach (var run in new[] { submitted, (await service.GetAsync(51))!, (await service.GetDetailsAsync(51))!.Run, (await service.ListAsync()).Single() })
        {
            JsonSerializer.Serialize(run).Should().NotContain(sentinel).And.NotContain("garden rain");
            using var projection = JsonDocument.Parse(run.InputsJson!);
            projection.RootElement.GetProperty("phrase").GetString().Should().Be("[REDACTED]");
            projection.RootElement.GetProperty("empty").GetString().Should().Be("");
            projection.RootElement.GetProperty("zero").GetInt32().Should().Be(0);
            projection.RootElement.GetProperty("flag").GetBoolean().Should().BeFalse();
            projection.RootElement.GetProperty("text").GetString().Should().Be("Useful 雨");
            run.OptionsJson.Should().NotContain("garden rain");
        }
    }

    [Fact]
    public async Task Metadata_survives_definition_edits_and_execution_uses_original_typed_payload()
    {
        await using var db = CreateDb();
        db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Inert fixture" });
        db.JobParameters.AddRange(
            new() { Id = 42, JobId = 41, Name = "phrase", Type = "secret" },
            new() { Id = 43, JobId = 41, Name = "vault", Type = "secret_reference" });
        await db.SaveChangesAsync();
        var provider = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var service = new JobRunService(db, provider);
        const string inputs = "{\"input\":{\"phrase\":\"garden rain Ω 908\",\"vault\":\"owned-target-vault-ref\",\"zero\":0,\"flag\":false},\"meta\":{\"requestId\":\"fixture-1\"}}";
        var command = new UpsertJobRunCommand(51, 41, 7, "", "fixture", JobRunState.Pending,
            0, DateTimeOffset.UtcNow, null, null, null, inputs, "{\"expectedRuntimeSeconds\":30}");
        var first = await service.UpsertRunAsync(command);
        first.SubmittedDefinitionRevision.Should().NotBeNullOrEmpty();
        first.InputProtectionStatus.Should().Be("submitted-definition-v1");
        var stored = await db.JobRuns.SingleAsync();
        stored.ProtectedExecutionJson.Should().NotContain("garden rain");
        stored.InputSensitivityJson.Should().NotContain("garden rain");
        db.JobParameters.RemoveRange(db.JobParameters);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Jobs.RemoveRange(await db.Jobs.ToListAsync()); // Detached retained history with no source definition.
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var restored = new JobRunService(db, provider);
        var updated = await restored.UpsertRunAsync(command with { InputsJson = "{}", OptionsJson = "[]", Status = JobRunState.Succeeded });
        updated.SubmittedDefinitionRevision.Should().Be(first.SubmittedDefinitionRevision);
        updated.InputsJson.Should().Contain("owned-target-vault-ref").And.NotContain("garden rain");
        (await restored.GetExecutionInputsAsync(51)).Should().Be(inputs);
        (await restored.GetExecutionOptionsAsync(51)).Should().Be("{\"expectedRuntimeSeconds\":30}");
        stored = await db.JobRuns.SingleAsync();
        var transferred = stored.ProtectedExecutionJson;
        stored.TenantId = 8;
        await db.SaveChangesAsync();
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => restored.GetExecutionInputsAsync(51));
        denied.Message.Should().NotContain("garden rain");
        stored.ProtectedExecutionJson.Should().Be(transferred);
    }

    [Fact]
    public async Task Definition_edit_between_submission_and_persistence_cannot_downgrade_the_bound_secret()
    {
        await using var db = CreateDb();
        db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Inert fixture" });
        db.JobParameters.Add(new() { Id = 42, JobId = 41, Name = "phrase", Type = "secret" });
        await db.SaveChangesAsync();
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var submittedDefinition = (await new JobDefinitionService(db).GetDetailsAsync(41))!;
        service.BindSubmittedDefinition(51, submittedDefinition);
        (await db.JobParameters.SingleAsync()).Type = "string";
        await db.SaveChangesAsync();
        // Even a repeated binding to the changed definition cannot erase the first snapshot.
        service.BindSubmittedDefinition(51, (await new JobDefinitionService(db).GetDetailsAsync(41))!);
        var run = await service.UpsertRunAsync(new(51, 41, 7, "", "fixture", JobRunState.Pending, 0,
            DateTimeOffset.UtcNow, null, null, null, "{\"phrase\":\"garden rain Ω 908\"}", null));
        run.InputsJson.Should().Contain("[REDACTED]").And.NotContain("garden rain");
        (await db.JobRuns.SingleAsync()).InputSensitivityJson.Should().Contain("secret");
    }

    [Fact]
    public async Task Linked_diagnostics_errors_logs_and_MCP_projection_scrub_exact_values_without_name_heuristics()
    {
        await using var db = CreateDb();
        db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Inert fixture" });
        db.JobParameters.Add(new() { Id = 42, JobId = 41, Name = "phrase", Type = "secret" });
        await db.SaveChangesAsync();
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var now = DateTimeOffset.UtcNow;
        const string sentinel = "garden rain Ω 908";
        await service.UpsertRunAsync(new(51, 41, 7, "", "fixture", JobRunState.Failed, 1, now,
            now, now, "diagnostic: " + sentinel, JsonSerializer.Serialize(new { phrase = sentinel, normal = "useful" }), null));
        await service.UpsertStepRunAsync(new(52, 51, 1, JobStepRunState.Failed, 1, "owned-request", sentinel, now, now));
        var activity = await service.CreateTaskActivityAsync(new("owned-request", 51, 1, "", 7,
            "inert", "Failed", sentinel, now, now, ResultJson: JsonSerializer.Serialize(new { stdout = new[] { sentinel }, exitCode = 1 }), CommandLabel: sentinel));
        await service.AppendTaskLogAsync(new("owned-request", activity.Id, "", 7, "stdout", "echo " + sentinel, 1, now));
        var detail = (await service.GetDetailsAsync(51))!;
        var logs = await service.GetLogsByRequestIdAsync("owned-request");
        var mcp = NetRatel.API.Endpoints.Client.McpOperatorJobEndpoints.ProjectActivityLogs(detail.Activities.Single(), logs, 100);
        foreach (var projection in new object[] { detail, logs, mcp, (await service.GetActivityByIdAsync(activity.Id))!, (await service.ListTaskActivitiesAsync()) })
            JsonSerializer.Serialize(projection).Should().NotContain("garden rain");
        (await db.JobRuns.SingleAsync()).Error.Should().Be("diagnostic: [REDACTED]");
        (await db.JobTaskActivities.SingleAsync()).ResultJson.Should().NotContain("garden rain");
        (await db.JobTaskLogs.SingleAsync()).Message.Should().Be("echo [REDACTED]");
        var keyEcho = await service.UpdateTaskActivityStatusAsync(new("owned-request", "Failed", null, now,
            JsonSerializer.Serialize(new Dictionary<string, int> { [sentinel] = 1 })));
        keyEcho!.ResultJson.Should().Be("[REDACTED]");
        (await db.JobTaskActivities.SingleAsync()).ResultJson.Should().Be("[REDACTED]");
    }

    [Fact]
    public async Task Legacy_history_without_provenance_masks_all_inputs_and_recovers_original_material_without_execution()
    {
        await using var db = CreateDb();
        const string original = "{\"innocentName\":\"garden rain Ω 908\",\"flag\":false,\"input\":{\"text\":\"retained\"}}";
        db.JobRuns.Add(new() { Id = 51, JobId = 41, TenantId = 7, InputsJson = original,
            OptionsJson = "[{\"name\":\"innocentName\",\"value\":\"garden rain Ω 908\"}]", Error = "garden rain Ω 908" });
        db.JobTaskActivities.Add(new() { Id = 61, JobRunId = 51, TenantId = 7, RequestId = "legacy-task", ResultJson = "garden rain Ω 908" });
        db.JobTaskLogs.Add(new() { JobTaskActivityId = 61, TenantId = 7, RequestId = "legacy-task", Stream = "stdout", Message = "garden rain Ω 908" });
        await db.SaveChangesAsync();
        var provider = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var service = new JobRunService(db, provider);
        (await service.GetActivityByRequestIdAsync("legacy-task"))!.ResultJson.Should().Be("[REDACTED]");
        (await db.JobRuns.SingleAsync()).ProtectedExecutionJson.Should().NotBeNullOrEmpty("linked task inspection protects the parent run too");
        JsonSerializer.Serialize(await service.GetLogsByRequestIdAsync("legacy-task")).Should().NotContain("garden rain");
        var read = (await service.GetAsync(51))!;
        read.InputProtectionStatus.Should().Be("legacy-unknown-v1");
        read.SubmittedDefinitionRevision.Should().BeNull();
        JsonSerializer.Serialize(read).Should().NotContain("garden rain").And.NotContain("retained");
        (await service.GetExecutionInputsAsync(51)).Should().Be(original);
        (await db.JobRuns.SingleAsync()).ProtectedExecutionJson.Should().NotBeNullOrEmpty().And.NotContain("garden rain");
        (await new JobRunService(db, provider).GetExecutionInputsAsync(51)).Should().Be(original);
        var wrongKeys = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => wrongKeys.GetExecutionInputsAsync(51));
        failure.Message.Should().Contain("key ring").And.NotContain("garden rain");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_terminal_protection_rebinds_only_a_previously_matching_immutable_body(bool matching)
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        const string inputs = "{\"phrase\":\"garden rain Ω 908\"}";
        db.JobRuns.Add(new() { Id = 51, JobId = 41, TenantId = 7, StartedBy = "fixture", Status = (int)JobRunState.Failed,
            CreatedAtUtc = now, CompletedAtUtc = now, Error = "garden rain Ω 908", InputsJson = inputs });
        var original = new JobRunDetails(new(51, 41, 7, "", "fixture", JobRunState.Failed, 0, now, null, now,
            "garden rain Ω 908", inputs, null), [], []);
        var originalHash = JobRunProjectionHash.Compute(original);
        db.Set<JobRunControlRecord>().Add(new() { RunId = 51, TerminalReadyAtUtc = now,
            TerminalResultHash = matching ? originalHash : new string('0', 64) });
        await db.SaveChangesAsync();
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var safe = (await service.GetDetailsAsync(51))!;
        (await NetRatel.API.Services.Orchestration.OrchestrationCallbackProjection.TerminalReadyAsync(db, safe, default)).Should().Be(matching);
        var control = await db.Set<JobRunControlRecord>().SingleAsync();
        control.TerminalReadyAtUtc.Should().Be(now);
        if (matching)
        {
            control.TerminalResultHash.Should().Be(JobRunProjectionHash.Compute(safe)).And.NotBe(originalHash);
            (await db.JobRuns.SingleAsync()).InputSensitivityJson.Should().Contain(originalHash).And.Contain(control.TerminalResultHash!);
        }
        else control.TerminalResultHash.Should().Be(new string('0', 64));
        JsonSerializer.Serialize(safe).Should().NotContain("garden rain");
    }

    [Fact]
    public async Task Linked_request_duplicate_inputs_and_diagnostics_keep_the_submission_sensitivity()
    {
        await using var db = CreateDb();
        db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Inert fixture" });
        db.JobParameters.Add(new() { Id = 42, JobId = 41, Name = "phrase", Type = "secret" });
        await db.SaveChangesAsync();
        var service = new RequestService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var created = await service.CreateAsync(new("fixture", "", "41", "{\"phrase\":\"garden rain Ω 908\",\"normal\":false}", 7));
        db.JobParameters.RemoveRange(db.JobParameters);
        await db.SaveChangesAsync();
        await service.UpdateAsync(new(created.Id, null, null, null, "51", "Failed", "garden rain Ω 908", "garden rain Ω 908", null, ["garden rain Ω 908"]));
        foreach (var response in new object[] { created, (await service.GetAsync(created.Id))!, await service.ListAsync() })
            JsonSerializer.Serialize(response).Should().NotContain("garden rain");
        var record = await db.Requests.SingleAsync();
        record.ProtectedJobInputsJson.Should().NotContain("garden rain");
        record.JobInputs.Should().Contain("false").And.Contain("[REDACTED]");
        record.ResultData.Should().Be("[REDACTED]");
    }

    [Fact]
    public async Task Dispatch_payloads_remain_exact_while_debug_logs_carry_only_execution_metadata()
    {
        await using var db = CreateDb();
        db.Scripts.Add(new() { Id = 61, Name = "Owned inert script", ScriptType = "Bash", Content = "true" });
        await db.SaveChangesAsync();
        var logger = new BridgeLogger();
        var bridge = new NetRatel.API.Services.Jobs.JobTaskBridge(new ScriptService(db), logger);
        var run = new JobRunInfo(51, 41, 7, "", "fixture", JobRunState.Pending, 0,
            DateTimeOffset.UtcNow, null, null, null, null, null);
        var inputs = new Dictionary<string, object?> { ["phrase"] = "garden rain Ω 908", ["flag"] = false, ["zero"] = 0 };
        foreach (var step in new[]
        {
            new JobStepInfo(52, 41, 1, JobStepKind.RunCommand, "bash", "printf '%s' '@option.phrase@'", null, null, true),
            new JobStepInfo(53, 41, 2, JobStepKind.LibraryScript, "bash", null, 61, null, true)
        })
        {
            var invocation = await bridge.BuildGatewayInvocationAsync(run, step, inputs,
                NetRatel.API.Services.Jobs.JobExecutionRuntimePolicy.FromOptions(null), default);
            invocation.Success.Should().BeTrue();
            invocation.PayloadJson.Should().Contain("garden rain");
        }
        logger.Entries.Should().NotBeEmpty();
        string.Join("\n", logger.Entries).Should().NotContain("garden rain");
    }

    private sealed class BridgeLogger : Microsoft.Extensions.Logging.ILogger<NetRatel.API.Services.Jobs.JobTaskBridge>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                foreach (var field in properties) Entries.Add(JsonSerializer.Serialize(field.Value, field.Value?.GetType() ?? typeof(object)));
            if (exception is not null) Entries.Add(exception.ToString());
        }
    }

    [Theory]
    [InlineData("  garden rain Ω 908  ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("\"garden rain Ω 908\"")]
    [InlineData("{\"a\":1}")]
    public async Task Server_error_normalization_cannot_expose_a_secret_text_value(string secret)
    {
        await using var db = CreateDb();
        db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Inert fixture" });
        db.JobParameters.Add(new() { Id = 42, JobId = 41, Name = "phrase", Type = "secret" });
        await db.SaveChangesAsync();
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var run = await service.UpsertRunAsync(new(51, 41, 7, "", "fixture", JobRunState.Failed, 0,
            DateTimeOffset.UtcNow, null, null, secret, JsonSerializer.Serialize(new { phrase = secret }), null));
        run.Error.Should().Be("[REDACTED]");
        (await service.GetExecutionInputsAsync(51)).Should().Be(JsonSerializer.Serialize(new { phrase = secret }));
    }

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
