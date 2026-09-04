using Microsoft.Extensions.Options;
using PCConnect.Core;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Security;
using Shouldly;

namespace PCConnect.UnitTests;

public class PasswordPolicyTests
{
    [Fact]
    public void Accepts_a_reasonable_passphrase() =>
        Should.NotThrow(() => PasswordPolicy.Validate("correct horse battery staple"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchar")]
    public void Refuses_anything_under_twelve_characters(string? password)
    {
        var error = Should.Throw<AppException>(() => PasswordPolicy.Validate(password));
        error.Code.ShouldBe(ErrorCodes.AuthPasswordPolicy);
        error.Status.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public void Refuses_a_password_containing_the_username() =>
        Should.Throw<AppException>(() => PasswordPolicy.Validate("adamkhattab2026!", "adamkhattab"));

    [Fact]
    public void Refuses_a_password_containing_the_email() =>
        Should.Throw<AppException>(() => PasswordPolicy.Validate("me@example.com-password", "user", "me@example.com"));

    [Fact]
    public void Refuses_a_single_repeated_character() =>
        Should.Throw<AppException>(() => PasswordPolicy.Validate(new string('a', 40)));

    [Fact]
    public void Refuses_something_absurdly_long() =>
        Should.Throw<AppException>(() => PasswordPolicy.Validate(new string('a', 300)));

    [Fact]
    public void Only_the_first_five_hex_characters_of_the_digest_leave_the_process()
    {
        var (prefix, suffix) = PasswordPolicy.PwnedRange("correct horse battery staple");

        prefix.Length.ShouldBe(5);
        suffix.Length.ShouldBe(35);
        prefix.ShouldAllBe(c => Uri.IsHexDigit(c));
    }
}

public class Argon2PasswordHasherTests
{
    private static Argon2PasswordHasher Create(int memoryKib = 19456, int timeCost = 2) =>
        new(Options.Create(new Argon2Options { MemoryKib = memoryKib, TimeCost = timeCost }));

    [Fact]
    public void Verifies_a_password_it_hashed()
    {
        var hasher = Create();
        var hash = hasher.Hash("correct horse battery staple");

        hasher.Verify("correct horse battery staple", hash).ShouldBeTrue();
        hasher.Verify("Correct horse battery staple", hash).ShouldBeFalse();
        hasher.Verify(string.Empty, hash).ShouldBeFalse();
    }

    [Fact]
    public void Emits_a_phc_string_carrying_its_parameters()
    {
        // The parameters travel with the hash, so they can be raised later
        // without a flag day (ADR-0002).
        var hash = Create().Hash("correct horse battery staple");

        hash.ShouldStartWith("$argon2id$v=19$m=19456,t=2,p=1$");
        hash.Split('$', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(5);
    }

    [Fact]
    public void Salts_every_hash()
    {
        var hasher = Create();
        hasher.Hash("same password").ShouldNotBe(hasher.Hash("same password"));
    }

    [Fact]
    public void Asks_for_a_rehash_when_the_stored_parameters_are_weaker_than_the_policy()
    {
        var weak = Create(memoryKib: 19456, timeCost: 2).Hash("correct horse battery staple");
        var stricter = Create(memoryKib: 32768, timeCost: 3);

        stricter.NeedsRehash(weak).ShouldBeTrue();
        stricter.NeedsRehash(stricter.Hash("correct horse battery staple")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$")]
    [InlineData("$argon2id$v=19$m=x,t=2,p=1$c2FsdA$aGFzaA")]
    [InlineData("$bcrypt$v=19$m=19456,t=2,p=1$c2FsdA$aGFzaA")]
    [InlineData("5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8")]
    public void Treats_anything_it_cannot_parse_as_a_failure_not_a_crash(string stored)
    {
        var hasher = Create();

        hasher.Verify("anything", stored).ShouldBeFalse();
        hasher.NeedsRehash(stored).ShouldBeTrue();
    }

    [Fact]
    public void Refuses_parameters_below_the_owasp_floor()
    {
        Should.Throw<InvalidOperationException>(() => new Argon2Options { MemoryKib = 4096 }.Validate());
        Should.Throw<InvalidOperationException>(() => new Argon2Options { TimeCost = 1 }.Validate());
        Should.NotThrow(() => new Argon2Options().Validate());
    }
}

public class PairingCodeTests
{
    [Fact]
    public void Generates_a_grouped_eight_character_code()
    {
        var code = PairingCode.Generate();

        code.Length.ShouldBe(9);
        code[4].ShouldBe('-');
        code.Replace("-", string.Empty, StringComparison.Ordinal).ShouldAllBe(c => PairingCode.Alphabet.Contains(c));
    }

    [Fact]
    public void Excludes_the_characters_people_confuse()
    {
        // 0/O, 1/I/L, 2/Z, 5/S, 8/B are all absent: the code is read aloud and
        // typed by a person (03 §2.6).
        foreach (var confusable in "01258OILSBZ")
        {
            PairingCode.Alphabet.ShouldNotContain(confusable);
        }
    }

    [Theory]
    [InlineData("ACDE-FGHJ", "ACDE-FGHJ")]
    [InlineData("acde-fghj", "ACDE-FGHJ")]
    [InlineData("ACDEFGHJ", "ACDE-FGHJ")]
    [InlineData("  acde fghj  ", "ACDE-FGHJ")]
    [InlineData("ACDE--FGHJ", "ACDE-FGHJ")]
    public void Accepts_what_a_person_actually_types(string input, string expected) =>
        PairingCode.Normalise(input).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("TOO-SHORT")]
    [InlineData("ACDEFGHJKLMN")]
    [InlineData("00000000")]
    public void Rejects_anything_that_is_not_a_code(string? input) =>
        PairingCode.Normalise(input).ShouldBeEmpty();

    [Fact]
    public void Generates_distinct_codes()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => PairingCode.Generate()).ToHashSet(StringComparer.Ordinal);
        codes.Count.ShouldBe(200);
    }
}

public class ScopeTests
{
    [Fact]
    public void Command_issue_and_command_receive_are_disjoint()
    {
        // The property the whole credential design rests on: nothing holds both,
        // so a stolen phone token can ask for a shutdown but never receive one
        // (03 §2.3).
        Scopes.UserSession.ShouldContain(Scopes.CommandIssue);
        Scopes.UserSession.ShouldNotContain(Scopes.CommandReceive);

        Scopes.DeviceSession.ShouldContain(Scopes.CommandReceive);
        Scopes.DeviceSession.ShouldNotContain(Scopes.CommandIssue);
    }

    [Fact]
    public void A_device_session_cannot_read_a_reminder_or_manage_the_account()
    {
        Scopes.DeviceSession.ShouldNotContain(Scopes.ReminderRead);
        Scopes.DeviceSession.ShouldNotContain(Scopes.ReminderWrite);
        Scopes.DeviceSession.ShouldNotContain(Scopes.AccountManage);
        Scopes.DeviceSession.ShouldNotContain(Scopes.DeviceManage);
    }

    [Fact]
    public void The_legacy_shim_is_the_one_credential_that_holds_both_and_it_is_documented()
    {
        // ADR-0008: the installed clients both issue and poll from one API key.
        // That weakness is carried forward deliberately, confined to the shim.
        Scopes.LegacyCompat.ShouldContain(Scopes.CommandIssue);
        Scopes.LegacyCompat.ShouldContain(Scopes.CommandReceive);

        // Even so, it cannot manage the account or pair a new device.
        Scopes.LegacyCompat.ShouldNotContain(Scopes.AccountManage);
        Scopes.LegacyCompat.ShouldNotContain(Scopes.DeviceManage);
    }

    [Fact]
    public void Require_throws_a_403_with_the_scope_named()
    {
        var caller = new CallerIdentity
        {
            UserId = 1,
            UserPublicId = Guid.CreateVersion7(),
            ClientKind = ClientKinds.Mobile,
            Scopes = [Scopes.ReminderRead],
            TokenId = "jti",
        };

        caller.Has(Scopes.ReminderRead).ShouldBeTrue();

        var error = Should.Throw<AppException>(() => caller.Require(Scopes.CommandIssue));
        error.Status.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        error.Code.ShouldBe(ErrorCodes.AuthScopeInsufficient);
        error.Message.ShouldContain(Scopes.CommandIssue);
    }
}

public class NormaliseTests
{
    [Theory]
    [InlineData("Adam@Example.COM", "adam@example.com")]
    [InlineData("  spaced@example.com  ", "spaced@example.com")]
    public void Lowercases_and_trims_email(string input, string expected) =>
        Normalise.Email(input).ShouldBe(expected);

    [Theory]
    [InlineData("5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8", true)]
    [InlineData("5E884898DA28047151D0E56F8DC6292773603D0D6AABBDD62A11EF721D1542D8", false)]
    [InlineData("short", false)]
    [InlineData("zzzz4898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8", false)]
    public void Recognises_the_shape_of_a_legacy_client_side_hash(string input, bool expected) =>
        Normalise.LooksLikeLegacyHash(input).ShouldBe(expected);

    [Fact]
    public void Sha256_hex_matches_what_the_legacy_clients_send() =>
        // Both v1 clients compute SHA-256 and hex-encode it lower case.
        Normalise.Sha256Hex("password")
            .ShouldBe("5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8");

    [Theory]
    [InlineData("Europe/London", "Europe/London")]
    [InlineData("Asia/Kolkata", "Asia/Kolkata")]
    [InlineData("Mars/Olympus", "Europe/London")]
    [InlineData("", "Europe/London")]
    [InlineData(null, "Europe/London")]
    public void Falls_back_to_a_known_zone_for_anything_unrecognised(string? input, string expected) =>
        Normalise.IanaTimeZoneOrDefault(input).ShouldBe(expected);

    [Theory]
    [InlineData("me@example.com", true)]
    [InlineData("me@sub.example.co.uk", true)]
    [InlineData("username", false)]
    [InlineData("@example.com", false)]
    [InlineData("me@example", false)]
    public void Distinguishes_an_email_from_a_username(string input, bool expected) =>
        Normalise.LooksLikeEmail(input).ShouldBe(expected);
}
