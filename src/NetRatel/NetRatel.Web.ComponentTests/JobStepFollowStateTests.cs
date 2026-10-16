using AwesomeAssertions;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Web.Components.Pages.Jobs;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class JobStepFollowStateTests
{
    private static readonly JobRunDto Run = new(9, 42, "Fixture", 7, "agent", "ui", JobRunStatusDto.Running, 1, 0, 100, null, null, "{}");
    private static JobStepRunDto Step(ulong id, int ordinal, JobStepRunStatusDto status) => new(id, 9, id, status, ordinal, $"task-{id}", status == JobStepRunStatusDto.Pending ? null : (long)id * 100, null, null);

    [Fact]
    public void Three_steps_follow_transitions_replays_gaps_and_final_output()
    {
        var follow = new JobStepFollowState();
        var first = Step(1, 1, JobStepRunStatusDto.Running);
        var second = Step(2, 2, JobStepRunStatusDto.Pending);
        var third = Step(3, 3, JobStepRunStatusDto.Pending);
        follow.Reconcile(Run, [first, second, third]).Should().Be(1);
        follow.Following.Should().BeTrue();
        follow.Reconcile(Run, [first, second, third]).Should().BeNull();
        first = first with { Status = JobStepRunStatusDto.Succeeded };
        follow.Reconcile(Run, [first, second, third]).Should().BeNull();
        follow.TargetId.Should().Be(1);
        second = Step(2, 2, JobStepRunStatusDto.Running);
        follow.Reconcile(Run, [first, second, third]).Should().Be(2);
        third = Step(3, 3, JobStepRunStatusDto.Running);
        second = second with { Status = JobStepRunStatusDto.Succeeded };
        follow.Reconcile(Run, [first, second, third]).Should().Be(3);
        follow.Reconcile(Run with { Status = JobRunStatusDto.Succeeded }, [first, second, third with { Status = JobStepRunStatusDto.Succeeded }]).Should().BeNull();
        follow.TargetId.Should().Be(3);
    }

    [Theory]
    [InlineData(JobStepRunStatusDto.Failed, JobRunStatusDto.Failed)]
    [InlineData(JobStepRunStatusDto.Cancelled, JobRunStatusDto.Cancelled)]
    [InlineData(JobStepRunStatusDto.TimedOut, JobRunStatusDto.TimedOut)]
    public void Direct_open_terminal_failure_skips_pending_and_manual_pause_resume_is_immediate(JobStepRunStatusDto status, JobRunStatusDto runStatus)
    {
        var follow = new JobStepFollowState();
        var first = Step(1, 1, JobStepRunStatusDto.Succeeded);
        var second = Step(2, 2, JobStepRunStatusDto.Running);
        var skipped = Step(3, 3, JobStepRunStatusDto.Skipped);
        follow.Reconcile(Run, [first, second, skipped]).Should().Be(2);
        follow.Pause();
        var terminal = Run with { Status = runStatus };
        second = second with { Status = status };
        follow.Reconcile(terminal, [first, second, skipped]).Should().BeNull();
        follow.Resume(terminal, [first, second, skipped]).Should().Be(2);
        var historical = new JobStepFollowState();
        historical.Reconcile(terminal, [first, second, skipped]).Should().Be(2);
        historical.Following.Should().BeFalse();
        historical.Reconcile(terminal, [first, second, skipped]).Should().BeNull();
    }

    [Fact]
    public void Concurrent_running_target_is_stable_retry_is_meaningful_and_fast_terminal_transition_uses_last_execution()
    {
        var follow = new JobStepFollowState();
        var first = Step(1, 1, JobStepRunStatusDto.Running);
        var second = Step(2, 2, JobStepRunStatusDto.Running);
        follow.Reconcile(Run, [second]).Should().Be(2);
        follow.Reconcile(Run, [first, second]).Should().BeNull();
        follow.Reconcile(Run, [first, second with { TaskRequestId = "retry", StartedAtMicros = 900 }]).Should().Be(2);
        follow.Reconcile(Run with { Status = JobRunStatusDto.Succeeded }, [first with { Status = JobStepRunStatusDto.Succeeded },
            second with { Status = JobStepRunStatusDto.Succeeded }, Step(3, 3, JobStepRunStatusDto.Succeeded), Step(4, 4, JobStepRunStatusDto.Skipped)]).Should().Be(3);
        follow.Reconcile(Run with { Id = 10 }, [first]).Should().BeNull();
        follow.TargetId.Should().BeNull();
    }
}
