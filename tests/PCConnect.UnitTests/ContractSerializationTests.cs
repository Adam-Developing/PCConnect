using System.Text.Json;
using PCConnect.Contracts.V2;
using Xunit;

namespace PCConnect.UnitTests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void ProfileUpdateDistinguishesOmittedDateFromExplicitNull()
    {
        var omitted = JsonSerializer.Deserialize<ProfileUpdate>("{\"displayName\":\"Ada\"}", JsonOptions)!;
        var cleared = JsonSerializer.Deserialize<ProfileUpdate>("{\"dateOfBirth\":null}", JsonOptions)!;
        var set = JsonSerializer.Deserialize<ProfileUpdate>("{\"dateOfBirth\":\"2000-01-02\"}", JsonOptions)!;

        Assert.False(omitted.DateOfBirth.IsSpecified);
        Assert.True(cleared.DateOfBirth.IsSpecified);
        Assert.Null(cleared.DateOfBirth.Value);
        Assert.Equal(new DateOnly(2000, 1, 2), set.DateOfBirth.Value);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
