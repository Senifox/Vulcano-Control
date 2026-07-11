using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vulcano_Control.ViewModels;

/// <summary>Backs the one-shot "Mit Server verbinden" dialog - collects host/port/PIN.</summary>
public partial class NetworkJoinViewModel : ObservableObject
{
    public event EventHandler? Confirmed;
    public event EventHandler? Cancelled;

    [ObservableProperty]
    private string host;

    [ObservableProperty]
    private int port;

    [ObservableProperty]
    private string pin;

    [ObservableProperty]
    private string? errorMessage;

    public NetworkJoinViewModel(string defaultHost, int defaultPort, string defaultPin)
    {
        host = defaultHost;
        port = defaultPort;
        pin = defaultPin;
    }

    [RelayCommand]
    private void Connect()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            ErrorMessage = "Bitte eine Server-Adresse angeben.";
            return;
        }
        if (Port is < 1 or > 65535)
        {
            ErrorMessage = "Port muss zwischen 1 und 65535 liegen.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Pin))
        {
            ErrorMessage = "Bitte die PIN des Servers eingeben.";
            return;
        }

        ErrorMessage = null;
        Confirmed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);
}
