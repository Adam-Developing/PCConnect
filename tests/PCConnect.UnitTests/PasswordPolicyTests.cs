using PCConnect.Domain;
using Xunit;

namespace PCConnect.UnitTests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("twelve chars\n")]
    public void NewPasswordsRejectWeakOrControlCharacterInput(string password) =>
        Assert.Throws<ArgumentException>(() => PasswordPolicy.ValidateNew(password));

    [Fact]
    public void NewPasswordsAcceptLongPassphrases() =>
        PasswordPolicy.ValidateNew("correct horse battery staple");
}
