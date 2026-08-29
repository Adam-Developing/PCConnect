using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PCConnect.Windows.Companion;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private string status = "Waiting for the authenticated PCConnect Windows service.";
    public string Status { get => status; private set { status = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public void ShowReminder(string text)
    {
        Status = text;
        Show();
        Activate();
    }

    public void ShowStatus(string text)
    {
        Status = text;
        Show();
        Activate();
    }

    private void DismissClick(object sender, RoutedEventArgs e) => Hide();
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}
