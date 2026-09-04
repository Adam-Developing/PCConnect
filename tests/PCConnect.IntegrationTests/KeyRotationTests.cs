using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using PCConnect.DbMigrator;
using PCConnect.Infrastructure.Security;
using Shouldly;

namespace PCConnect.IntegrationTests;

/// <summary>
/// Finishing a key encryption key rotation (ADR-0004, runbook §5).
///
/// Rotation is only half a procedure until every data key has been rewrapped:
/// while any user's DEK is still under the previous KEK, removing that key
/// destroys their reminders. These tests are about the step that ends the
/// rotation, and about it being safe to run against a live database.
/// </summary>
[Collection(ApiCollection.Name)]
public class KeyRotationTests(ApiFixture fixture)
{
    private const string Old = "test-old";
    private const string New = "test-new";

    private static EnvelopeEncryptor Encryptor(string current, params string[] ids)
    {
        var options = new EnvelopeOptions { CurrentKekId = current };

        foreach (var id in ids)
        {
            // Deterministic per id so two encryptors built in one test agree on
            // what the key for "test-old" is.
            options.Keys[id] = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)));
        }

        return new EnvelopeEncryptor(Options.Create(options));
    }

    /// <summary>
    /// Creates a user carrying a data key wrapped with <paramref name="kekId"/>,
    /// and returns the plaintext DEK so a test can prove it survived.
    /// </summary>
    private async Task<(long UserId, byte[] DataKey)> UserWithDataKeyAsync(EnvelopeEncryptor envelope, string kekId)
    {
        var (wrapped, id) = envelope.CreateDataKey();
        id.ShouldBe(kekId);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var name = $"rot-{Guid.NewGuid():N}"[..24];
        var email = $"{name}@example.test";

        var userId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO users (public_id, username, username_normalised, email, email_normalised,
                               timezone, dek_wrapped, dek_kek_id)
            VALUES (uuidv7(), @Name, lower(@Name), @Email, lower(@Email),
                    'Europe/London', @Wrapped, @KekId)
            RETURNING id
            """,
            new
            {
                Name = name,
                Email = email,
                Wrapped = wrapped,
                KekId = kekId,
            });

        return (userId, envelope.UnwrapDataKey(wrapped, kekId));
    }

    private async Task<(byte[] Wrapped, string KekId)> ReadKeyAsync(long userId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<(byte[] Wrapped, string KekId)>("""
            SELECT dek_wrapped AS "Wrapped", dek_kek_id AS "KekId" FROM users WHERE id = @Id
            """, new { Id = userId });
    }

    [Fact]
    public async Task Rewrapping_moves_the_key_without_changing_it()
    {
        var underOld = Encryptor(Old, Old);
        var (userId, dataKey) = await UserWithDataKeyAsync(underOld, Old);

        var rotating = Encryptor(New, Old, New);
        await new KekRotation(fixture.ConnectionString).RewrapAsync(rotating);

        var (wrapped, kekId) = await ReadKeyAsync(userId);
        kekId.ShouldBe(New);

        // The wrapper changed; the key inside did not. That is what makes a
        // rotation cheap — no reminder is re-encrypted, because the thing they
        // were encrypted with is the same bytes.
        var onlyNew = Encryptor(New, New);
        onlyNew.UnwrapDataKey(wrapped, kekId).ShouldBe(dataKey);
    }

    [Fact]
    public async Task Everything_readable_moves_and_what_is_left_is_named()
    {
        var underOld = Encryptor(Old, Old);
        await UserWithDataKeyAsync(underOld, Old);

        var rotating = Encryptor(New, Old, New);
        var rotation = new KekRotation(fixture.ConnectionString);

        var before = await rotation.StatusAsync(rotating);
        before.ByKekId.ShouldContainKey(Old);
        KekRotation.Describe(before, New).ShouldContain("Keep the previous key configured");

        var after = await rotation.RewrapAsync(rotating);

        // Everything this run could open is now under the new key. Anything
        // still on an older one is there because its KEK is not configured —
        // the rotation is finished, not stalled, and the two are different
        // states an operator has to be able to tell apart.
        after.ByKekId.ShouldNotContainKey(Old);
        after.Remaining.ShouldBe(after.Unreadable.Values.Sum());
    }

    /// <summary>
    /// The database this runs against also holds users imported from v1, whose
    /// data keys are wrapped with a KEK this test does not have. That is not an
    /// awkward test fixture — it is exactly the production hazard: one user
    /// whose key is gone must not stop everyone else's rotation.
    /// </summary>
    [Fact]
    public async Task One_unreadable_key_does_not_block_the_others()
    {
        var mine = Encryptor(Old, Old);
        var (userId, _) = await UserWithDataKeyAsync(mine, Old);

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();

            // A second user wrapped with a key nobody has any more.
            var stranded = await UserWithDataKeyAsync(Encryptor("lost", "lost"), "lost");
            stranded.UserId.ShouldNotBe(userId);
        }

        var rotating = Encryptor(New, Old, New);
        var progress = await new KekRotation(fixture.ConnectionString).RewrapAsync(rotating);

        progress.Rewrapped.ShouldBeGreaterThan(0);
        progress.Unreadable.ShouldContainKey("lost");

        var (_, kekId) = await ReadKeyAsync(userId);
        kekId.ShouldBe(New);

        KekRotation.Describe(progress, New).ShouldContain("which is not configured");
    }

    [Fact]
    public async Task Rewrapping_twice_changes_nothing_the_second_time()
    {
        var underOld = Encryptor(Old, Old);
        var (userId, _) = await UserWithDataKeyAsync(underOld, Old);

        var rotating = Encryptor(New, Old, New);
        var rotation = new KekRotation(fixture.ConnectionString);

        await rotation.RewrapAsync(rotating);
        var first = await ReadKeyAsync(userId);

        // An operator who is unsure whether the run finished must be able to
        // run it again. A second pass has nothing to do and must not rewrap a
        // key that is already current — that would be a write with no reader.
        var second = await rotation.RewrapAsync(rotating);

        // Nothing about this user changed on the second pass: a key already
        // under the current KEK is not re-wrapped, so the stored bytes are
        // identical rather than merely equivalent.
        var unchanged = await ReadKeyAsync(userId);
        unchanged.Wrapped.ShouldBe(first.Wrapped);
        unchanged.KekId.ShouldBe(New);
        second.ByKekId.ShouldNotContainKey(Old);
    }

    [Fact]
    public async Task A_missing_previous_key_leaves_the_row_alone_and_says_so()
    {
        var underOld = Encryptor(Old, Old);
        var (userId, _) = await UserWithDataKeyAsync(underOld, Old);

        // The operator removed the previous key too early. Silently re-keying a
        // DEK that cannot be unwrapped would turn a recoverable mistake into an
        // unrecoverable one, so the row is left exactly as it is and reported.
        var withoutOld = Encryptor(New, New);

        var progress = await new KekRotation(fixture.ConnectionString).RewrapAsync(withoutOld);

        progress.Unreadable.ShouldContainKey(Old);
        KekRotation.Describe(progress, New).ShouldContain(Old);

        var (wrapped, kekId) = await ReadKeyAsync(userId);
        kekId.ShouldBe(Old);
        underOld.UnwrapDataKey(wrapped, kekId).Length.ShouldBe(32);
    }
}
