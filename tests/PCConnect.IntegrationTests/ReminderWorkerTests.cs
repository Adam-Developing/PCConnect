extern alias worker;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Infrastructure.Reminders;
using PCConnect.Infrastructure.Security;
using PCConnect.Infrastructure.Identity;
using Xunit;

namespace PCConnect.IntegrationTests;

public sealed class ReminderWorkerTests(PostgreSqlApiFixture fixture) : IClassFixture<PostgreSqlApiFixture>
{
    private static readonly string[] ReminderCapability = ["reminders"];

    [Fact]
    public async Task SchedulerCoversAllSelectedLateAndOfflineDevicesAndAcknowledgement()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        var now = DateTimeOffset.Parse("2026-08-27T12:05:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var clock = new FixedClock(now);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var workerInstance = new worker::PCConnect.Worker.ReminderSchedulerWorker(
            dataSource,
            clock,
            NullLogger<worker::PCConnect.Worker.ReminderSchedulerWorker>.Instance);

        var userId = Guid.CreateVersion7(now);
        var allDeviceOne = Guid.CreateVersion7(now.AddMilliseconds(1));
        var selectedDevice = Guid.CreateVersion7(now.AddMilliseconds(2));
        var incapableDevice = Guid.CreateVersion7(now.AddMilliseconds(3));
        var allReminder = Guid.CreateVersion7(now.AddMilliseconds(4));
        var selectedReminder = Guid.CreateVersion7(now.AddMilliseconds(5));
        var cipher = new ReminderCipher(SecurityOptionsForTest());
        var allEncrypted = cipher.Encrypt(allReminder, userId, "all-device reminder");
        var selectedEncrypted = cipher.Encrypt(selectedReminder, userId, "selected reminder");

        await SeedAsync(dataSource, userId, allDeviceOne, selectedDevice, incapableDevice,
            allReminder, selectedReminder, allEncrypted, selectedEncrypted, now);

        await workerInstance.GenerateOccurrencesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await DeliveryCountAsync(dataSource, allReminder));
        Assert.Equal(1, await DeliveryCountAsync(dataSource, selectedReminder));

        var lateDevice = Guid.CreateVersion7(now.AddMilliseconds(6));
        await InsertDeviceAsync(dataSource, userId, lateDevice, "late", reminders: true);
        await workerInstance.SynchronizeAllDeviceDeliveriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, await DeliveryCountAsync(dataSource, allReminder));
        Assert.Equal(1, await DeliveryCountAsync(dataSource, selectedReminder));

        await workerInstance.MakeDueDeliveriesAvailableAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, await StatusCountAsync(dataSource, allReminder, "available"));
        Assert.Equal(1, await StatusCountAsync(dataSource, selectedReminder, "pending"));

        var available = await new ReminderService(dataSource, cipher, clock)
            .ListAvailableDeliveriesAsync(allDeviceOne, null, 50, TestContext.Current.CancellationToken);
        var delivery = Assert.Single(available.Items);
        Assert.Equal("all-device reminder", delivery.Text);
        await new ReminderService(dataSource, cipher, clock).AcknowledgeDeliveryAsync(
            allDeviceOne,
            delivery.Id,
            new ReminderAcknowledgement("completed", now),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, await DirectStatusCountAsync(dataSource, delivery.Id, "completed"));
        var acknowledgedReminder = await new ReminderService(dataSource, cipher, clock)
            .GetAsync(userId, allReminder, TestContext.Current.CancellationToken);
        Assert.Equal("completed", acknowledgedReminder.LastAcknowledgementStatus);
        Assert.Equal(now, acknowledgedReminder.LastAcknowledgedAt);
        Assert.Equal("Offline all", acknowledgedReminder.LastAcknowledgedBy);
        await Assert.ThrowsAsync<ConflictException>(() =>
            new ReminderService(dataSource, cipher, clock).AcknowledgeDeliveryAsync(
                allDeviceOne,
                delivery.Id,
                new ReminderAcknowledgement("completed", now),
                TestContext.Current.CancellationToken));

        var metrics = new worker::PCConnect.Worker.OperationalMetricsWorker(
            dataSource,
            NullLogger<worker::PCConnect.Worker.OperationalMetricsWorker>.Instance);
        await metrics.RefreshAsync(TestContext.Current.CancellationToken);

        var configuration = new ConfigurationBuilder().Build();
        var publisherOne = new worker::PCConnect.Worker.OutboxPublisherWorker(
            dataSource, clock, configuration, NullLogger<worker::PCConnect.Worker.OutboxPublisherWorker>.Instance);
        var publisherTwo = new worker::PCConnect.Worker.OutboxPublisherWorker(
            dataSource, clock, configuration, NullLogger<worker::PCConnect.Worker.OutboxPublisherWorker>.Instance);
        await using (var makeOutboxAvailable = dataSource.CreateCommand("UPDATE outbox_messages SET available_at=@now WHERE published_at IS NULL"))
        {
            makeOutboxAvailable.Parameters.AddWithValue("now", now);
            await makeOutboxAvailable.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var firstClaims = await publisherOne.ClaimAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(firstClaims);
        Assert.Empty(await publisherTwo.ClaimAsync(TestContext.Current.CancellationToken));
        await using (var expireClaims = dataSource.CreateCommand("UPDATE outbox_messages SET claimed_until=@expired WHERE published_at IS NULL"))
        {
            expireClaims.Parameters.AddWithValue("expired", now.AddSeconds(-1));
            await expireClaims.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(firstClaims.Count, (await publisherTwo.ClaimAsync(TestContext.Current.CancellationToken)).Count);
    }

    private static async Task SeedAsync(NpgsqlDataSource source, Guid userId, Guid allDeviceOne, Guid selectedDevice,
        Guid incapableDevice, Guid allReminder, Guid selectedReminder, EncryptedReminder allEncrypted,
        EncryptedReminder selectedEncrypted, DateTimeOffset now)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO users(id,username,email,display_name,timezone,created_at,updated_at)
            VALUES(@userId,@username,@email,'Reminder Test','Europe/London',@now,@now);
            INSERT INTO devices(id,user_id,platform,display_name,display_name_normalized,agent_version,protocol_version,capabilities,status)
            VALUES
              (@allDeviceOne,@userId,'windows','Offline all','offline-all','test',2,ARRAY['reminders']::device_capability[],'offline'),
              (@selectedDevice,@userId,'windows','Selected','selected','test',2,ARRAY['reminders']::device_capability[],'online'),
              (@incapableDevice,@userId,'windows','No reminders','no-reminders','test',2,ARRAY['lock']::device_capability[],'online');
            INSERT INTO reminders(id,user_id,target_mode,timezone,local_start,text_ciphertext,text_nonce,text_tag,wrapped_data_key,wrapping_key_id,created_at,updated_at)
            VALUES
              (@allReminder,@userId,'all_devices','Europe/London','2026-08-27 13:00:00',@allCipher,@allNonce,@allTag,@allWrapped,@keyId,@now,@now),
              (@selectedReminder,@userId,'selected_devices','Europe/London','2026-08-28 13:00:00',@selectedCipher,@selectedNonce,@selectedTag,@selectedWrapped,@keyId,@now,@now);
            INSERT INTO reminder_targets(reminder_id,device_id) VALUES(@selectedReminder,@selectedDevice);
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("username", "reminder-" + userId.ToString("N"));
        command.Parameters.AddWithValue("email", userId.ToString("N") + "@example.test");
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("allDeviceOne", allDeviceOne);
        command.Parameters.AddWithValue("selectedDevice", selectedDevice);
        command.Parameters.AddWithValue("incapableDevice", incapableDevice);
        command.Parameters.AddWithValue("allReminder", allReminder);
        command.Parameters.AddWithValue("selectedReminder", selectedReminder);
        command.Parameters.AddWithValue("allCipher", allEncrypted.Ciphertext);
        command.Parameters.AddWithValue("allNonce", allEncrypted.Nonce);
        command.Parameters.AddWithValue("allTag", allEncrypted.Tag);
        command.Parameters.AddWithValue("allWrapped", allEncrypted.WrappedDataKey);
        command.Parameters.AddWithValue("selectedCipher", selectedEncrypted.Ciphertext);
        command.Parameters.AddWithValue("selectedNonce", selectedEncrypted.Nonce);
        command.Parameters.AddWithValue("selectedTag", selectedEncrypted.Tag);
        command.Parameters.AddWithValue("selectedWrapped", selectedEncrypted.WrappedDataKey);
        command.Parameters.AddWithValue("keyId", allEncrypted.WrappingKeyId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertDeviceAsync(NpgsqlDataSource source, Guid userId, Guid deviceId, string name, bool reminders)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO devices(id,user_id,platform,display_name,display_name_normalized,agent_version,protocol_version,capabilities,status)
            VALUES(@id,@userId,'windows',@name,@name,'test',2,@capabilities::device_capability[],'offline');
            """);
        command.Parameters.AddWithValue("id", deviceId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("capabilities", reminders ? ReminderCapability : Array.Empty<string>());
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static Task<long> DeliveryCountAsync(NpgsqlDataSource source, Guid reminderId) =>
        ScalarAsync(source, "SELECT count(*) FROM reminder_deliveries rd JOIN reminder_occurrences ro ON ro.id=rd.occurrence_id WHERE ro.reminder_id=@id", reminderId);

    private static Task<long> StatusCountAsync(NpgsqlDataSource source, Guid reminderId, string status) =>
        ScalarAsync(source, "SELECT count(*) FROM reminder_deliveries rd JOIN reminder_occurrences ro ON ro.id=rd.occurrence_id WHERE ro.reminder_id=@id AND rd.status::text=@status", reminderId, status);

    private static Task<long> DirectStatusCountAsync(NpgsqlDataSource source, Guid deliveryId, string status) =>
        ScalarAsync(source, "SELECT count(*) FROM reminder_deliveries WHERE id=@id AND status::text=@status", deliveryId, status);

    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql, Guid id, string? status = null)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        if (status is not null) command.Parameters.AddWithValue("status", status);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SecurityOptions SecurityOptionsForTest()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        return new SecurityOptions
        {
            TokenHashingKey = key,
            LegacyCredentialHashingKey = key,
            ActiveReminderKeyId = "test-v1",
            ReminderWrappingKeys = new() { ["test-v1"] = key },
            ActiveEmailKeyId = "test-v1",
            EmailEncryptionKeys = new() { ["test-v1"] = key },
            DeletionTombstoneKey = key,
            ExportEncryptionKey = key
        };
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
