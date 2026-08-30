using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PCConnect.Contracts.V2;

namespace PCConnect.Windows.Agent;

public sealed class AgentApiClient(HttpClient http, IDeviceCredentialStore store) : IDisposable
{
    private StoredDeviceCredential? credential;
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    public async Task<bool> IsEnrolledAsync(CancellationToken cancellationToken)
    {
        credential ??= await store.LoadAsync(cancellationToken);
        return credential is not null;
    }

    public Uri ApiBaseAddress => http.BaseAddress!;

    public async Task<Guid?> GetDeviceIdAsync(CancellationToken cancellationToken)
    {
        credential ??= await store.LoadAsync(cancellationToken);
        return credential?.DeviceId;
    }

    public void ForgetCredential() => credential = null;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await EnsureAccessAsync(cancellationToken);
        return credential!.AccessToken;
    }

    public async Task HeartbeatAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "agent/heartbeat", JsonContent.Create(new Heartbeat(
            instanceId, typeof(AgentApiClient).Assembly.GetName().Version?.ToString() ?? "2.0.0", 2,
            [DeviceCapability.Lock, DeviceCapability.Sleep, DeviceCapability.Hibernate, DeviceCapability.SignOut, DeviceCapability.Restart, DeviceCapability.Shutdown, DeviceCapability.Reminders], DateTimeOffset.UtcNow)), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Page<Command>> ListCommandsAsync(string? cursor, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"agent/commands?limit=50{(cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Page<Command>>(cancellationToken) ?? new([], null);
    }

    public async Task<Command?> ClaimAsync(Guid commandId, Guid instanceId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"agent/commands/{commandId:D}/claim", JsonContent.Create(new CommandClaim(instanceId)), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.Gone) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Command>(cancellationToken);
    }

    public async Task AcknowledgeAsync(Guid commandId, Guid instanceId, string state, CommandFailureCode? failureCode, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"agent/commands/{commandId:D}/acknowledgements",
            JsonContent.Create(new CommandAcknowledgement(state, instanceId, commandId, failureCode)), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<WindowsSidStatus> RegisterWindowsSidAsync(string sid, string? displayLabel, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "agent/windows-sid-candidates", JsonContent.Create(new WindowsSidCandidateRequest(sid, displayLabel)), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WindowsSidStatus>(cancellationToken)
            ?? throw new InvalidOperationException("The SID candidate endpoint returned no status.");
    }

    public async Task<Page<ReminderDelivery>> ListReminderDeliveriesAsync(string? cursor, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"agent/reminder-deliveries?limit=50{(cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Page<ReminderDelivery>>(cancellationToken) ?? new([], null);
    }

    public async Task AcknowledgeReminderAsync(Guid deliveryId, string state, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"reminder-deliveries/{deliveryId:D}/acknowledgements",
            JsonContent.Create(new ReminderAcknowledgement(state, DateTimeOffset.UtcNow)), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        await EnsureAccessAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential!.AccessToken);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        credential = credential with { AccessExpiresAt = DateTimeOffset.MinValue };
        await EnsureAccessAsync(cancellationToken);
        using var retry = new HttpRequestMessage(method, path) { Content = content };
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        return await http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task EnsureAccessAsync(CancellationToken cancellationToken)
    {
        credential ??= await store.LoadAsync(cancellationToken) ?? throw new InvalidOperationException("This agent is not enrolled.");
        if (credential.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return;
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (credential.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return;
            using var response = await http.PostAsJsonAsync("agent/auth/refresh", new RefreshRequest(credential.RefreshToken), cancellationToken);
            response.EnsureSuccessStatusCode();
            var rotated = await response.Content.ReadFromJsonAsync<DeviceTokenPair>(cancellationToken)
                ?? throw new InvalidOperationException("Device refresh returned no credential.");
            credential = new(rotated.DeviceId, rotated.AccessToken, rotated.AccessTokenExpiresAt, rotated.RefreshToken, rotated.RefreshTokenExpiresAt);
            await store.SaveAsync(credential, cancellationToken);
        }
        finally { refreshLock.Release(); }
    }

    public void Dispose()
    {
        refreshLock.Dispose();
        http.Dispose();
    }
}
