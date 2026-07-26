using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>One log line as the table shows it. The level stays English - it goes into the export
/// verbatim, and a translated level would not match what anyone quotes back in a bug report.</summary>
public sealed record LogRow(LogEntry Entry)
{
    public string Time => Entry.Timestamp.ToString("HH:mm:ss");

    public string Level => Entry.Level.ToString();

    public string Message => Entry.Message;
}

/// <summary>
/// The log, with a filter per level and a count next to each so it is obvious whether hiding Debug
/// is hiding two lines or two hundred.
/// </summary>
public partial class LogViewModel : ObservableObject, IDisposable
{
    private readonly LogService _log;

    [ObservableProperty]
    private bool _showDebug = true;

    [ObservableProperty]
    private bool _showInfo = true;

    [ObservableProperty]
    private bool _showWarning = true;

    [ObservableProperty]
    private bool _showError = true;

    [ObservableProperty]
    private string _exportNote = "";

    public LogViewModel(LogService log)
    {
        _log = log;

        // Seed from the snapshot: the log starts writing before this view model exists, and those
        // first lines - which device was found, what firmware - are the ones worth having.
        foreach (var entry in _log.Snapshot())
        {
            _all.Add(new LogRow(entry));
        }

        Rebuild();
        _log.EntryAdded += OnEntryAdded;
    }

    private readonly ObservableCollection<LogRow> _all = new();

    /// <summary>What the table binds to: the subset the filters allow, oldest first.</summary>
    public ObservableCollection<LogRow> Entries { get; } = new();

    public int DebugCount => Count(LogLevel.Debug);
    public int InfoCount => Count(LogLevel.Info);
    public int WarningCount => Count(LogLevel.Warning);
    public int ErrorCount => Count(LogLevel.Error);
    public int TotalCount => _all.Count;

    /// <summary>Raised after entries were added, so the view can keep the newest line in sight.</summary>
    public event EventHandler? EntriesAppended;

    private int Count(LogLevel level) => _all.Count(r => r.Entry.Level == level);

    partial void OnShowDebugChanged(bool value) => Rebuild();
    partial void OnShowInfoChanged(bool value) => Rebuild();
    partial void OnShowWarningChanged(bool value) => Rebuild();
    partial void OnShowErrorChanged(bool value) => Rebuild();

    private bool Passes(LogRow row) => row.Entry.Level switch
    {
        LogLevel.Debug => ShowDebug,
        LogLevel.Info => ShowInfo,
        LogLevel.Warning => ShowWarning,
        LogLevel.Error => ShowError,
        _ => true,
    };

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var row in _all.Where(Passes))
        {
            Entries.Add(row);
        }

        EntriesAppended?.Invoke(this, EventArgs.Empty);
    }

    private void OnEntryAdded(object? sender, LogEntry entry) =>
        Dispatcher.UIThread.Post(() =>
        {
            var row = new LogRow(entry);
            _all.Add(row);

            if (Passes(row))
            {
                Entries.Add(row);
                EntriesAppended?.Invoke(this, EventArgs.Empty);
            }

            RaiseCounts();
        });

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(DebugCount));
        OnPropertyChanged(nameof(InfoCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(TotalCount));
    }

    /// <summary>
    /// Writes everything - not just what the filters show - next to the settings, which is where the
    /// Settings tab says exported logs live.
    /// </summary>
    [RelayCommand]
    private void Export()
    {
        var path = Path.Combine(
            AppPaths.DataDirectory,
            $"vulcano-control-log-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");

        try
        {
            var text = new StringBuilder();
            foreach (var row in _all)
            {
                text.AppendLine($"[{row.Time}] [{row.Level}] {row.Message}");
            }

            File.WriteAllText(path, text.ToString());
            ExportNote = Strings.Get("Log.Exported", path);
            _log.Log(Strings.Get("Log.LogExported", path));
        }
        catch (Exception ex)
        {
            ExportNote = Strings.Get("Log.ExportFailed", ex.Message);
        }
    }

    public void Dispose() => _log.EntryAdded -= OnEntryAdded;

    /// <summary>Re-reads every computed label. Called after a language change; passing a
    /// null property name is the framework's "all of them" signal.</summary>
    public void RefreshText() => OnPropertyChanged(new PropertyChangedEventArgs(null));
}

