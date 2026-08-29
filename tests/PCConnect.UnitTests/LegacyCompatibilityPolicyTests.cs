using PCConnect.Infrastructure.Compatibility;
using PCConnect.Infrastructure.Identity;
using Xunit;

namespace PCConnect.UnitTests;

public sealed class LegacyCompatibilityPolicyTests
{
    private static readonly DateTimeOffset Cutover = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Day44AllowsLegacyControllerLock()
    {
        LegacyCompatibilityPolicy.EnsureAvailable(Cutover, Cutover.AddDays(44).AddHours(23), commandCreation: true);
        Assert.Equal("lock", LegacyCompatibilityPolicy.EnsureCommandAllowed(", LOCK "));
    }

    [Fact]
    public void HighRiskCommandsAlwaysRequireMigration()
    {
        var exception = Assert.Throws<ConflictException>(() => LegacyCompatibilityPolicy.EnsureCommandAllowed("shutdown"));
        Assert.Equal("migration_required", exception.Code);
    }

    [Fact]
    public void Day45DeniesControllerButAllowsAgentPolling()
    {
        var exception = Assert.Throws<ResourceGoneException>(() => LegacyCompatibilityPolicy.EnsureAvailable(Cutover, Cutover.AddDays(45), commandCreation: true));
        Assert.Equal("legacy_command_creation_disabled", exception.Code);
        LegacyCompatibilityPolicy.EnsureAvailable(Cutover, Cutover.AddDays(45), commandCreation: false);
    }

    [Fact]
    public void Day60DeniesEveryLegacyRoute()
    {
        var exception = Assert.Throws<ResourceGoneException>(() => LegacyCompatibilityPolicy.EnsureAvailable(Cutover, Cutover.AddDays(60), commandCreation: false));
        Assert.Equal("legacy_compatibility_expired", exception.Code);
    }
}
