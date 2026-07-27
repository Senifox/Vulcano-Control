using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.App.Services;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>Where an update has got to. Drives one sentence and two buttons.</summary>
public enum UpdateState
{
    /// <summary>This copy cannot update itself: the debugger, or the portable build.</summary>
    Unsupported,

    /// <summary>Nothing asked yet.</summary>
    Idle,
    Checking,
    UpToDate,
    Downloading,

    /// <summary>Downloaded and waiting. Installs itself when the app closes.</summary>
    Ready,

    /// <summary>The check or the download did not come off - almost always no network.</summary>
    Failed
}

/// <summary>
/// Keeping the app up to date, and the one rule that matters while doing it: an update is never
/// applied out from under a running ramp.
///
/// 1.x checked at startup, downloaded, and restarted the app there and then. That was fine for an
/// app you sat in front of, and would not be fine here - a ramp is half an hour of a device heating
/// on its own, and the app is what turns the heater off at the end of it. So the automatic path
/// stops at "downloaded": it is installed when the app is closed anyway. Restarting now is a button,
/// and that button is unavailable while a ramp runs.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateSource _source;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    private UpdateState _state = UpdateState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _version = "";

    /// <summary>Why the last attempt failed, shown as part of the status line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _error = "";

    /// <summary>
    /// Set by the shell, the same way the other tabs learn about a run. Restarting is refused while
    /// this is true, and the note next to the button says why.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRestartBlocked))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    private bool _isRampRunning;

    /// <param name="source">Where versions come from. Everything worth logging happens inside it,
    /// which is why this view model has no log of its own.</param>
    public UpdateViewModel(IUpdateSource source)
    {
        _source = source;

        if (!_source.IsSupported)
        {
            _state = UpdateState.Unsupported;
            return;
        }

        // A version downloaded in an earlier session that never got applied - the app was closed
        // some other way than through the exit that would have installed it. Come up saying so
        // rather than fetching the same 57 MB again.
        if (_source.PendingVersion is { } pending)
        {
            _version = pending;
            _state = UpdateState.Ready;
        }
    }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    public bool IsReady => State == UpdateState.Ready;

    /// <summary>True when there is something to install but this is not the moment.</summary>
    public bool IsRestartBlocked => IsReady && IsRampRunning;

    public string StatusText => State switch
    {
        UpdateState.Unsupported => Strings.Get("Update.Unsupported"),
        UpdateState.Checking => Strings.Get("Update.Checking"),
        UpdateState.UpToDate => Strings.Get("Update.UpToDate"),
        UpdateState.Downloading => Strings.Get("Update.Downloading", Version),
        UpdateState.Ready => Strings.Get("Update.Ready", Version),
        UpdateState.Failed => Strings.Get("Update.Failed", Error),
        _ => "",
    };

    /// <summary>
    /// The startup pass: check, and if there is something, fetch it quietly. Nothing is installed
    /// and nothing is asked - the next time the app is closed it will be a version newer.
    /// </summary>
    public async Task RunStartupCheckAsync(bool automatic)
    {
        if (!automatic || State is UpdateState.Unsupported or UpdateState.Ready) return;

        // Checking already fetches what it finds, so this is the whole startup pass. It used to
        // download again afterwards - twice on a good day, and once even after a check that had
        // failed, which reported a version as ready that had never been looked for.
        await CheckAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (!_source.IsSupported) return;

        State = UpdateState.Checking;
        Error = "";

        var result = await _source.CheckAsync();

        if (result.DidFail)
        {
            Error = result.Error ?? "";
            State = UpdateState.Failed;
            return;
        }

        if (!result.Available)
        {
            State = UpdateState.UpToDate;
            return;
        }

        Version = result.Version!;

        // A manual check downloads on its own too: somebody who asked whether there is a new
        // version has already said what they want to happen next.
        await DownloadAsync();
    }

    private bool CanCheck() => State is not (UpdateState.Unsupported or UpdateState.Checking or UpdateState.Downloading);

    private async Task DownloadAsync()
    {
        State = UpdateState.Downloading;

        if (await _source.DownloadAsync())
        {
            State = UpdateState.Ready;
        }
        else
        {
            Error = Strings.Get("Update.Failed.Download");
            State = UpdateState.Failed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart()
    {
        // Checked again, not just declared. CanExecute governs whether the button is available;
        // ICommand.Execute does not consult it, so a binding, a shortcut or a later caller can walk
        // straight past it. For "do not restart mid-ramp" that is not a risk worth carrying.
        if (!CanRestart()) return;

        _source.ApplyAndRestart();
    }

    private bool CanRestart() => IsReady && !IsRampRunning;

    /// <summary>
    /// Called when the app is on its way out. Hands a downloaded update to the installer, which
    /// waits for this process to end and then applies it.
    /// </summary>
    public void ApplyOnExit()
    {
        if (State != UpdateState.Ready) return;

        _source.ApplyOnExit();
    }

    /// <summary>Re-reads the status line after a language change.</summary>
    public void RefreshText() => OnPropertyChanged(nameof(StatusText));
}
