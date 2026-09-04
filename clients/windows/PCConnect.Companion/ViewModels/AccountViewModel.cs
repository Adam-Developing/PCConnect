using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PCConnect.Client;
using PCConnect.Core.Contracts;

namespace PCConnect.Companion.ViewModels;

public partial class AccountViewModel(
    PcConnectClient api,
    ILogger<AccountViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _timezone = string.Empty;

    [ObservableProperty]
    private bool _isEmailVerified;

    [ObservableProperty]
    private string _pairingCode = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The two letters in the sidebar's avatar.</summary>
    public string Initials
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;

            var letters = source
                .Split([' ', '.', '_', '-', '@'], StringSplitOptions.RemoveEmptyEntries)
                .Take(2)
                .Select(part => char.ToUpperInvariant(part[0]));

            var initials = string.Concat(letters);
            return initials.Length == 0 ? "?" : initials;
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            if (await api.GetProfileAsync() is { } profile)
            {
                Apply(profile);
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            logger.LogWarning(ex, "Profile load failed");
        }
    }

    private void Apply(ProfileResponse profile)
    {
        DisplayName = profile.DisplayName;
        OnPropertyChanged(nameof(Initials));
        Email = profile.Email;
        Timezone = profile.Timezone;
        IsEmailVerified = profile.IsEmailVerified;
    }

    /// <summary>
    /// Confirms the code the agent is showing on this PC. This is the user half
    /// of pairing: nothing about a machine's name grants it anything (C-2).
    /// </summary>
    [RelayCommand]
    private async Task ClaimPairingAsync()
    {
        if (string.IsNullOrWhiteSpace(PairingCode))
        {
            StatusMessage = "Type the code the PCConnect agent is showing.";
            return;
        }

        IsBusy = true;

        try
        {
            var claimed = await api.ClaimPairingAsync(PairingCode.Trim());
            StatusMessage = claimed is null
                ? "That code was not accepted."
                : $"Paired {claimed.DisplayName}.";
            PairingCode = string.Empty;
        }
        catch (PcConnectApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "Could not reach the PCConnect server.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
