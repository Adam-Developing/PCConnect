using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
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

}
