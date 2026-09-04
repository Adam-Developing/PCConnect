using Microsoft.Extensions.Logging.Abstractions;
using PCConnect.Agent.Execution;
using PCConnect.Client;
using PCConnect.Core.Domain;
using Shouldly;

namespace PCConnect.UnitTests;

/// <summary>
/// The agent-side checks of 03 §3 (6, 7 and 8), tested without executing
/// anything: the bridge is a stub and no power command is reachable from a
/// rejected input by construction.
/// </summary>
public class CommandExecutorTests
{
    private sealed class RecordingBridge : ISessionCommandBridge
    {
        public List<string> Calls { get; } = [];

        public Task<ExecutionResult> LockAsync(CancellationToken ct = default)
        {
            Calls.Add("lock");
            return Task.FromResult(ExecutionResult.Ok("locked"));
        }

        public Task<ExecutionResult> SignOutAsync(CancellationToken ct = default)
        {
            Calls.Add("signout");
            return Task.FromResult(ExecutionResult.Ok("signed out"));
        }
    }

    private static (CommandExecutor Executor, RecordingBridge Bridge) Create()
    {
        var bridge = new RecordingBridge();
        return (new CommandExecutor(NullLogger<CommandExecutor>.Instance, bridge), bridge);
    }

    private static DateTimeOffset Fresh => DateTimeOffset.UtcNow.AddMinutes(2);

    private static DateTimeOffset Stale => DateTimeOffset.UtcNow.AddMinutes(-2);

    [Theory]
    [InlineData("shutdown; rm -rf /")]
    [InlineData("C:\\Windows\\System32\\shutdown.exe /s")]
    [InlineData("lock\0")]
    [InlineData("ѕhutdown")]
    [InlineData("format c:")]
    [InlineData("")]
    public async Task Rejects_anything_outside_the_allow_list_and_runs_nothing(string commandType)
    {
        var (executor, bridge) = Create();

        var result = await executor.ExecuteAsync(Guid.CreateVersion7().ToString(), commandType, Fresh);

        result.Outcome.ShouldBe(ExecutionOutcome.Rejected);
        result.ResultCode.ShouldBe("unsupported");
        bridge.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_a_command_that_arrived_after_its_ttl()
    {
        var (executor, bridge) = Create();

        // Check 7. The agent does not trust the server's word about freshness:
        // this is the fix for "the shutdown I sent this morning fired when I
        // opened my laptop tonight" (S2-03).
        var result = await executor.ExecuteAsync(Guid.CreateVersion7().ToString(), CommandTypes.Lock, Stale);

        result.Outcome.ShouldBe(ExecutionOutcome.Rejected);
        result.ResultCode.ShouldBe("expired");
        bridge.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Judges_freshness_against_server_time_not_the_local_clock()
    {
        var (executor, bridge) = Create();

        // A machine whose clock is two hours fast would otherwise treat every
        // command as expired (05 §8).
        executor.ServerClockOffset = TimeSpan.FromHours(-2);

        var expiresAt = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(1);
        var result = await executor.ExecuteAsync(Guid.CreateVersion7().ToString(), CommandTypes.Lock, expiresAt);

        result.Outcome.ShouldBe(ExecutionOutcome.Succeeded);
        bridge.Calls.ShouldBe(["lock"]);
    }

    [Fact]
    public async Task Executes_a_command_once_even_if_it_arrives_twice()
    {
        var (executor, bridge) = Create();
        var commandId = Guid.CreateVersion7().ToString();

        // Check 8. A push racing a poll delivers the same command twice; the
        // second one must not lock the machine again (05 §4.3).
        var first = await executor.ExecuteAsync(commandId, CommandTypes.Lock, Fresh);
        var second = await executor.ExecuteAsync(commandId, CommandTypes.Lock, Fresh);

        first.Outcome.ShouldBe(ExecutionOutcome.Succeeded);
        second.Outcome.ShouldBe(ExecutionOutcome.Rejected);
        second.ResultCode.ShouldBe("duplicate");
        bridge.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_replay_guard_is_bounded()
    {
        var (executor, _) = Create();

        var first = Guid.CreateVersion7().ToString();
        await executor.ExecuteAsync(first, CommandTypes.Lock, Fresh);
        executor.HasExecuted(first).ShouldBeTrue();

        // An agent that runs for months must not accumulate every id it has
        // ever seen; the oldest fall out of the window.
        for (var i = 0; i < 300; i++)
        {
            await executor.ExecuteAsync(Guid.CreateVersion7().ToString(), CommandTypes.Lock, Fresh);
        }

        executor.HasExecuted(first).ShouldBeFalse();
    }

    [Fact]
    public async Task Hands_session_bound_commands_to_the_companion()
    {
        var (executor, bridge) = Create();

        await executor.ExecuteAsync(Guid.CreateVersion7().ToString(), CommandTypes.Lock, Fresh);
        await executor.ExecuteAsync(Guid.CreateVersion7().ToString(), CommandTypes.SignOut, Fresh);

        // A service in session 0 has no interactive session of its own
        // (ADR-0012).
        bridge.Calls.ShouldBe(["lock", "signout"]);
    }
}

public class FallbackPollingPolicyTests
{
    [Fact]
    public void A_connected_agent_does_not_poll_at_all()
    {
        FallbackPollingPolicy.ShouldPoll(socketHealthy: true).ShouldBeFalse();
        FallbackPollingPolicy.ShouldPoll(socketHealthy: false).ShouldBeTrue();
    }

    [Fact]
    public void Doubles_the_interval_up_to_the_cap()
    {
        var policy = new FallbackPollingPolicy();

        policy.Current.ShouldBe(TimeSpan.FromSeconds(5));

        policy.NextInterval();
        policy.Current.ShouldBe(TimeSpan.FromSeconds(10));

        policy.NextInterval();
        policy.Current.ShouldBe(TimeSpan.FromSeconds(20));

        policy.NextInterval();
        policy.Current.ShouldBe(TimeSpan.FromSeconds(30));

        policy.NextInterval();
        policy.Current.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Resets_to_the_base_interval()
    {
        var policy = new FallbackPollingPolicy();
        policy.NextInterval();
        policy.NextInterval();

        policy.Reset();
        policy.Current.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Applies_jitter_within_twenty_percent()
    {
        // Without this, a server restart makes every agent in the fleet
        // reconnect on the same tick (05 §5).
        var interval = TimeSpan.FromSeconds(10);

        FallbackPollingPolicy.Jitter(interval, 1.0).ShouldBe(TimeSpan.FromSeconds(12));
        FallbackPollingPolicy.Jitter(interval, -1.0).ShouldBe(TimeSpan.FromSeconds(8));
        FallbackPollingPolicy.Jitter(interval, 0).ShouldBe(interval);
    }

    [Fact]
    public void Random_jitter_stays_inside_the_band_and_is_not_constant()
    {
        var interval = TimeSpan.FromSeconds(10);
        var samples = Enumerable.Range(0, 200).Select(_ => FallbackPollingPolicy.Jitter(interval)).ToList();

        samples.ShouldAllBe(s => s >= TimeSpan.FromSeconds(8) && s <= TimeSpan.FromSeconds(12));
        samples.Distinct().Count().ShouldBeGreaterThan(50);
    }

    [Fact]
    public void Never_returns_a_non_positive_delay() =>
        FallbackPollingPolicy.Jitter(TimeSpan.FromMilliseconds(1), -1.0)
            .ShouldBeGreaterThan(TimeSpan.Zero);
}
