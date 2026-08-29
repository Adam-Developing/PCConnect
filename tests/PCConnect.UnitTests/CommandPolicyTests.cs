using PCConnect.Contracts.V2;
using PCConnect.Domain.Commands;
using Xunit;

namespace PCConnect.UnitTests;

public sealed class CommandPolicyTests
{
    [Theory]
    [InlineData(CommandType.Sleep)]
    [InlineData(CommandType.Hibernate)]
    [InlineData(CommandType.SignOut)]
    [InlineData(CommandType.Restart)]
    [InlineData(CommandType.Shutdown)]
    public void DestructiveCommandsRequireStepUp(CommandType command) => Assert.True(CommandPolicy.RequiresStepUp(command));

    [Fact]
    public void LockRequiresExplicitConfirmation()
    {
        var exception = Assert.Throws<DomainException>(() => CommandPolicy.Validate(new(CommandType.Lock), [DeviceCapability.Lock]));
        Assert.Equal("confirmation_required", exception.Code);
    }

    [Fact]
    public void StateMachineMatchesCanonicalTransitions()
    {
        Assert.True(CommandPolicy.CanTransition(CommandStatus.Queued, CommandStatus.Claimed));
        Assert.True(CommandPolicy.CanTransition(CommandStatus.Claimed, CommandStatus.Accepted));
        Assert.True(CommandPolicy.CanTransition(CommandStatus.Accepted, CommandStatus.Succeeded));
        Assert.False(CommandPolicy.CanTransition(CommandStatus.Queued, CommandStatus.Succeeded));
        Assert.False(CommandPolicy.CanTransition(CommandStatus.Succeeded, CommandStatus.Queued));
    }
}
