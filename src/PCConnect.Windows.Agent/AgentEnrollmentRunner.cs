using System.Net;
using System.Net.Http.Json;
using PCConnect.Contracts.V2;

namespace PCConnect.Windows.Agent;

public sealed class AgentEnrollmentRunner(HttpClient http, IDeviceCredentialStore store)
{
    private static readonly DeviceCapability[] Capabilities =
    [
        DeviceCapability.Lock, DeviceCapability.Sleep, DeviceCapability.Hibernate, DeviceCapability.SignOut,
        DeviceCapability.Restart, DeviceCapability.Shutdown, DeviceCapability.Reminders
    ];

    public async Task RunAsync(string displayName, CancellationToken cancellationToken)
    {
        if (await store.LoadAsync(cancellationToken) is not null) throw new InvalidOperationException("This agent is already enrolled. Revoke it before enrolling again.");
        using var create = await http.PostAsJsonAsync("device-enrollments", new DeviceEnrollmentRequest(
            PlatformType.Windows, displayName, typeof(AgentEnrollmentRunner).Assembly.GetName().Version?.ToString() ?? "2.0.0",
            2, Capabilities, TimeZoneInfo.Local.Id), cancellationToken);
        create.EnsureSuccessStatusCode();
        var enrollment = await create.Content.ReadFromJsonAsync<DeviceEnrollment>(cancellationToken)
            ?? throw new InvalidOperationException("The enrollment endpoint returned no challenge.");
        Console.WriteLine($"Approve code {enrollment.UserCode} at {enrollment.VerificationUri}. It expires at {enrollment.ExpiresAt:u}.");

        while (DateTimeOffset.UtcNow < enrollment.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(enrollment.PollIntervalSeconds), cancellationToken);
            using var response = await http.PostAsJsonAsync("device-enrollments/token", new DeviceCodeRequest(enrollment.DeviceCode), cancellationToken);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests) continue;
            if (response.StatusCode == HttpStatusCode.Gone) throw new InvalidOperationException("The enrollment expired or was already consumed.");
            response.EnsureSuccessStatusCode();
            var pair = await response.Content.ReadFromJsonAsync<DeviceTokenPair>(cancellationToken)
                ?? throw new InvalidOperationException("The enrollment exchange returned no credential.");
            await store.SaveAsync(new(pair.DeviceId, pair.AccessToken, pair.AccessTokenExpiresAt, pair.RefreshToken, pair.RefreshTokenExpiresAt), cancellationToken);
            Console.WriteLine($"Device {pair.DeviceId:D} enrolled. The PCConnect Agent v2 service will connect automatically.");
            return;
        }
        throw new TimeoutException("The enrollment was not approved before it expired.");
    }
}
