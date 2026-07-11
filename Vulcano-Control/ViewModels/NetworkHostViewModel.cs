using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vulcano_Control.ViewModels;

/// <summary>Backs the one-shot "Server starten" dialog - collects port/PIN, shows the LAN
/// address(es) to hand to other participants.</summary>
public partial class NetworkHostViewModel : ObservableObject
{
    public event EventHandler? Confirmed;
    public event EventHandler? Cancelled;

    [ObservableProperty]
    private int port;

    [ObservableProperty]
    private string pin;

    [ObservableProperty]
    private string? errorMessage;

    public string LocalAddressesDisplay { get; }

    public NetworkHostViewModel(int defaultPort, string defaultPin)
    {
        port = defaultPort;
        pin = defaultPin;
        LocalAddressesDisplay = DescribeLocalAddresses();
    }

    private static string DescribeLocalAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(addr => addr.Address.ToString())
                .Distinct()
                .ToList();

            return addresses.Count > 0 ? string.Join(", ", addresses) : "Nicht ermittelbar";
        }
        catch
        {
            return "Nicht ermittelbar";
        }
    }

    [RelayCommand]
    private void Start()
    {
        if (Port is < 1 or > 65535)
        {
            ErrorMessage = "Port muss zwischen 1 und 65535 liegen.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Pin))
        {
            ErrorMessage = "Bitte eine PIN vergeben.";
            return;
        }

        ErrorMessage = null;
        Confirmed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);
}
