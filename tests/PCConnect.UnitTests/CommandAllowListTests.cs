using PCConnect.Core.Domain;
using Shouldly;

namespace PCConnect.UnitTests;

/// <summary>
/// The allow-list deserves the strictest test in the codebase (06 §7): for
/// every input outside the six known types, assert that nothing is accepted —
/// including inputs with shell metacharacters, path separators, null bytes and
/// unicode case-folding tricks.
/// </summary>
public class CommandAllowListTests
{
    [Theory]
    [InlineData("shutdown")]
    [InlineData("Shutdown")]
    [InlineData("SHUTDOWN")]
    [InlineData("Shut_Down")]
    [InlineData("  shutdown  ")]
    public void Accepts_the_known_spellings_of_a_known_command(string input)
    {
        CommandTypes.TryNormalise(input, out var normalised).ShouldBeTrue();
        normalised.ShouldBe(CommandTypes.Shutdown);
    }

    [Theory]
    [InlineData("Logoff", CommandTypes.SignOut)]
    [InlineData("logout", CommandTypes.SignOut)]
    [InlineData("Signout", CommandTypes.SignOut)]
    [InlineData("Sign_Out", CommandTypes.SignOut)]
    [InlineData("reboot", CommandTypes.Restart)]
    [InlineData("Restart", CommandTypes.Restart)]
    [InlineData("Lock", CommandTypes.Lock)]
    [InlineData("Sleep", CommandTypes.Sleep)]
    [InlineData("Hibernate", CommandTypes.Hibernate)]
    public void Maps_the_legacy_vocabulary_onto_the_v2_one(string input, string expected)
    {
        CommandTypes.TryNormalise(input, out var normalised).ShouldBeTrue();
        normalised.ShouldBe(expected);
    }

    [Theory]
    // shell metacharacters
    [InlineData("shutdown; rm -rf /")]
    [InlineData("shutdown && calc.exe")]
    [InlineData("shutdown | net user")]
    [InlineData("shutdown`whoami`")]
    [InlineData("shutdown$(whoami)")]
    [InlineData("shutdown & shutdown")]
    [InlineData("shutdown\nrestart")]
    [InlineData("shutdown\r\nrestart")]
    // path separators and traversal
    [InlineData("../shutdown")]
    [InlineData("..\\shutdown")]
    [InlineData("C:\\Windows\\System32\\shutdown.exe")]
    [InlineData("/bin/shutdown")]
    // null bytes and control characters
    [InlineData("shutdown\0")]
    [InlineData("\0shutdown")]
    [InlineData("shut\0down")]
    // unicode case-folding and homoglyph tricks against a naive
    // case-insensitive comparison
    [InlineData("ShutdowN\u0131")]      // dotless i
    [InlineData("\u015Ehutdown")]       // S with cedilla
    [InlineData("ѕhutdown")]            // Cyrillic es
    [InlineData("shutdowｎ")]            // fullwidth n
    [InlineData("ⓢhutdown")]            // circled s
    // padding and near-misses
    [InlineData("shutdownn")]
    [InlineData("shut down")]
    [InlineData("shutdow")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("format")]
    [InlineData("exec")]
    public void Rejects_everything_outside_the_allow_list(string input)
    {
        CommandTypes.TryNormalise(input, out var normalised).ShouldBeFalse();
        normalised.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("shutdown\t")]
    [InlineData("\tshutdown")]
    [InlineData("\r\nshutdown\r\n")]
    public void Trims_surrounding_whitespace_but_only_surrounding(string input)
    {
        // Trimming is safe because the value never reaches a command line: it
        // selects one of six enum members. Whitespace *inside* the value is a
        // different matter and is refused above.
        CommandTypes.TryNormalise(input, out var normalised).ShouldBeTrue();
        normalised.ShouldBe(CommandTypes.Shutdown);
    }

    [Fact]
    public void Rejects_null()
    {
        CommandTypes.TryNormalise(null, out var normalised).ShouldBeFalse();
        normalised.ShouldBeEmpty();
    }

    [Fact]
    public void The_vocabulary_is_exactly_six_types()
    {
        // A seventh type is a deliberate act: it needs a database CHECK change,
        // an agent implementation and a risk-tier decision. This test is here so
        // that adding one is never accidental.
        CommandTypes.All.Count.ShouldBe(6);
        CommandTypes.All.ShouldBe(new[] { "shutdown", "restart", "signout", "lock", "sleep", "hibernate" }, ignoreOrder: true);
    }

    [Theory]
    [InlineData(CommandTypes.Shutdown, RiskTiers.Destructive)]
    [InlineData(CommandTypes.Restart, RiskTiers.Destructive)]
    [InlineData(CommandTypes.SignOut, RiskTiers.Destructive)]
    [InlineData(CommandTypes.Hibernate, RiskTiers.Destructive)]
    [InlineData(CommandTypes.Lock, RiskTiers.Standard)]
    [InlineData(CommandTypes.Sleep, RiskTiers.Standard)]
    public void Assigns_the_risk_tier_that_matches_the_consequence(string type, string tier) =>
        CommandTypes.RiskTierFor(type).ShouldBe(tier);
}

public class CommandStateMachineTests
{
    [Theory]
    [InlineData(CommandStatuses.Issued, CommandStatuses.Delivered)]
    [InlineData(CommandStatuses.Issued, CommandStatuses.Expired)]
    [InlineData(CommandStatuses.Issued, CommandStatuses.Cancelled)]
    [InlineData(CommandStatuses.Delivered, CommandStatuses.Succeeded)]
    [InlineData(CommandStatuses.Delivered, CommandStatuses.Failed)]
    [InlineData(CommandStatuses.Delivered, CommandStatuses.Expired)]
    public void Allows_the_transitions_in_the_state_machine(string from, string to) =>
        CommandStateMachine.CanTransition(from, to).ShouldBeTrue();

    [Theory]
    [InlineData(CommandStatuses.Succeeded, CommandStatuses.Failed)]
    [InlineData(CommandStatuses.Failed, CommandStatuses.Succeeded)]
    [InlineData(CommandStatuses.Expired, CommandStatuses.Succeeded)]
    [InlineData(CommandStatuses.Expired, CommandStatuses.Delivered)]
    [InlineData(CommandStatuses.Cancelled, CommandStatuses.Delivered)]
    [InlineData(CommandStatuses.Delivered, CommandStatuses.Issued)]
    [InlineData(CommandStatuses.Delivered, CommandStatuses.Cancelled)]
    public void Refuses_everything_else(string from, string to) =>
        CommandStateMachine.CanTransition(from, to).ShouldBeFalse();

    [Fact]
    public void A_terminal_command_can_go_nowhere()
    {
        foreach (var terminal in CommandStatuses.Terminal)
        {
            CommandStateMachine.NextStates(terminal).ShouldBeEmpty();
        }
    }

    [Fact]
    public void An_ack_racing_delivery_is_still_legal()
    {
        // The push and its ack can beat the delivery write. Treating that as
        // illegal would lose the ack and let the command expire despite having
        // been executed (05 §4.3).
        CommandStateMachine.CanTransition(CommandStatuses.Issued, CommandStatuses.Succeeded).ShouldBeTrue();
        CommandStateMachine.CanTransition(CommandStatuses.Issued, CommandStatuses.Failed).ShouldBeTrue();
    }
}

public class CommandTtlTests
{
    [Fact]
    public void Defaults_to_two_minutes() =>
        CommandTtl.Resolve(null).ShouldBe(TimeSpan.FromSeconds(120));

    [Theory]
    [InlineData(5)]
    [InlineData(120)]
    [InlineData(600)]
    public void Accepts_values_inside_the_documented_range(int seconds) =>
        CommandTtl.Resolve(seconds).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(601)]
    [InlineData(-1)]
    [InlineData(86400)]
    public void Refuses_values_outside_it(int seconds)
    {
        var error = Should.Throw<AppException>(() => CommandTtl.Resolve(seconds));
        error.Code.ShouldBe(ErrorCodes.CommandTtlInvalid);
    }
}
