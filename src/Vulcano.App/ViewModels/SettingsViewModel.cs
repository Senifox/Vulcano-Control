using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.App.Services;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>A time-axis mode plus the name it goes by in the interface.</summary>
public sealed record TimeAxisOption(TimeAxisMode Mode, string Name);

/// <summary>
/// One version in the changelog list. The heading is assembled here rather than in the view so the
/// two halves - a version that is always a version, and a date that may be missing - do not become
/// a binding with a trailing separator hanging off it.
/// </summary>
public sealed class ChangelogEntryViewModel(ChangelogEntry entry)
{
    public string Heading => string.IsNullOrEmpty(entry.Date)
        ? entry.Version
        : $"{entry.Version} · {entry.Date}";

    public IReadOnlyList<string> Items => entry.Items;

    /// <summary>The version this app is running, called out in the list so it is obvious which of
    /// these you actually have.</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>
/// A language and its own name for itself. Deliberately not translated: a list that reads
/// "English / Deutsch" is legible to both readers, while "Englisch / Deutsch" is only legible to
/// one of them - and someone looking for their language is looking for the word they would use.
/// </summary>
public sealed record LanguageOption(AppLanguage Language, string Name);

/// <summary>
/// The Settings tab. No OK, no Cancel: every change writes through to settings.json as it is made,
/// which is the whole reason this stopped being a dialog.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly ThemeManager _themeManager;
    private readonly VolcanoDeviceOrchestrator _device;
    private readonly SoundService? _sound;
    private readonly INotifier? _notifier;

    /// <summary>True while the constructor is filling properties from the loaded settings, so
    /// they do not each save the file on the way in.</summary>
    private readonly bool _loading;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private AppLanguage _language;

    [ObservableProperty]
    private int _historyRetentionMinutes;

    [ObservableProperty]
    private int _rampPushThresholdCelsius;

    [ObservableProperty]
    private TimeAxisMode _timeAxisMode;

    [ObservableProperty]
    private bool _soundEnabled;

    [ObservableProperty]
    private bool _desktopNotifications;

    [ObservableProperty]
    private bool _automaticUpdates;

    [ObservableProperty]
    private int _newQuickTemperature = 195;

    public SettingsViewModel(
        SettingsService settingsService,
        AppSettings settings,
        ThemeManager themeManager,
        VolcanoDeviceOrchestrator device,
        SoundService? sound = null,
        INotifier? notifier = null,
        UpdateViewModel? update = null)
    {
        _loading = true;

        _settingsService = settingsService;
        _settings = settings;
        _themeManager = themeManager;
        _device = device;
        _sound = sound;
        _notifier = notifier;

        _theme = settings.Theme;
        _language = settings.Language;
        _historyRetentionMinutes = settings.HistoryRetentionMinutes;
        _rampPushThresholdCelsius = settings.RampPushThresholdCelsius;
        _timeAxisMode = settings.TimeAxisMode;
        _soundEnabled = settings.SoundEnabled;
        _desktopNotifications = settings.DesktopNotifications;
        _automaticUpdates = settings.AutomaticUpdates;

        QuickTemperatures = new ObservableCollection<int>(settings.PredefinedTemperatures);

        // The shell builds it and hands it over, because the window needs it too. A tab built on its
        // own in a test gets one that reports there is no update mechanism, which is true.
        Update = update ?? new UpdateViewModel(new NoUpdateSource());

        _loading = false;
    }

    /// <summary>Checking for and holding on to a new version, shown in the About card.</summary>
    public UpdateViewModel Update { get; }

    public ObservableCollection<int> QuickTemperatures { get; }

    public LanguageOption[] Languages { get; } =
    [
        new(AppLanguage.English, "English"),
        new(AppLanguage.German, "Deutsch"),
    ];

    /// <summary>The option object the combo box binds to; writing it sets <see cref="Language"/>.</summary>
    public LanguageOption? SelectedLanguage
    {
        get => Languages.FirstOrDefault(o => o.Language == Language);
        set
        {
            if (value is not null) Language = value.Language;
        }
    }

    /// <summary>The time-axis choices with the names the design uses - the enum spells them
    /// "Fixed15", which is not something to put in front of anyone. Rebuilt per read so a language
    /// change takes them along.</summary>
    public TimeAxisOption[] TimeAxisOptions =>
    [
        new(TimeAxisMode.FollowRun, Strings.Get("Settings.TimeAxis.FollowRun")),
        new(TimeAxisMode.Fixed15, Strings.Get("Settings.TimeAxis.Fixed15")),
        new(TimeAxisMode.Fixed60, Strings.Get("Settings.TimeAxis.Fixed60")),
        new(TimeAxisMode.Session, Strings.Get("Settings.TimeAxis.Session")),
    ];

    /// <summary>The option object the combo box binds to; writing it sets <see cref="TimeAxisMode"/>.</summary>
    public TimeAxisOption? SelectedTimeAxis
    {
        get => TimeAxisOptions.FirstOrDefault(o => o.Mode == TimeAxisMode);
        set
        {
            if (value is not null) TimeAxisMode = value.Mode;
        }
    }

    public string SettingsFilePath => _settingsService.SettingsFilePath;

    public string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public string BuildNote => "Avalonia 11 · net10.0";

    /// <summary>
    /// What changed, newest first. The unreleased section is left out here: it describes a build
    /// nobody running this has, and reading about changes you do not have is worse than not
    /// reading about them.
    /// </summary>
    public IReadOnlyList<ChangelogEntryViewModel> Changelog =>
        Core.Services.Changelog.Entries
            .Where(e => !e.IsUnreleased)
            .Select(e => new ChangelogEntryViewModel(e) { IsCurrent = e.Version == Version })
            .ToList();

    public bool HasChangelog => Core.Services.Changelog.Entries.Count > 0;

    partial void OnThemeChanged(AppTheme value)
    {
        if (_loading) return;

        _themeManager.Apply(value);
        _settings.Theme = value;
        Save();
    }

    partial void OnLanguageChanged(AppLanguage value)
    {
        if (_loading) return;

        // Rewrites every entry in the application's resources, which the labels are bound to
        // dynamically - so the interface changes language without a restart.
        Loc.Apply(value);
        _settings.Language = value;
        Save();

        // The combo box shows the names of these, and those names are themselves translated.
        OnPropertyChanged(nameof(TimeAxisOptions));
        OnPropertyChanged(nameof(SelectedTimeAxis));
        OnPropertyChanged(nameof(SelectedLanguage));
    }

    partial void OnHistoryRetentionMinutesChanged(int value) =>
        Persist(() => _settings.HistoryRetentionMinutes = value);

    partial void OnRampPushThresholdCelsiusChanged(int value) =>
        Persist(() =>
        {
            _settings.RampPushThresholdCelsius = value;
            // Takes effect on the running ramp too, not just the next one.
            _device.PushThresholdCelsius = value;
        });

    partial void OnTimeAxisModeChanged(TimeAxisMode value)
    {
        OnPropertyChanged(nameof(SelectedTimeAxis));
        Persist(() => _settings.TimeAxisMode = value);
    }

    // Both switches take effect at once rather than at the next launch, which is what a switch
    // labelled with what it does implies. They used to only write a settings file.
    partial void OnSoundEnabledChanged(bool value)
    {
        if (_sound is not null) _sound.SoundEnabled = value;
        Persist(() => _settings.SoundEnabled = value);
    }

    partial void OnDesktopNotificationsChanged(bool value)
    {
        if (_notifier is not null) _notifier.Enabled = value;
        Persist(() => _settings.DesktopNotifications = value);
    }

    /// <summary>Only governs the check that runs unasked at startup. The button next to it always
    /// works - switching this off means "do not go looking on your own", not "never update".</summary>
    partial void OnAutomaticUpdatesChanged(bool value) =>
        Persist(() => _settings.AutomaticUpdates = value);

    [RelayCommand]
    private void AddQuickTemperature()
    {
        var value = Math.Clamp(NewQuickTemperature, (int)RampValidation.MinCelsius, (int)RampValidation.MaxCelsius);
        if (QuickTemperatures.Contains(value)) return;

        var index = QuickTemperatures.Count(t => t < value);
        QuickTemperatures.Insert(index, value);
        PersistQuickTemperatures();
    }

    [RelayCommand]
    private void RemoveQuickTemperature(int celsius)
    {
        QuickTemperatures.Remove(celsius);
        PersistQuickTemperatures();
    }

    /// <summary>Opens the folder the settings live in, so "where is my configuration" has an
    /// answer that does not involve reading a path out loud.</summary>
    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
        }
        catch
        {
            // Nothing sensible to do if the shell refuses; the path is on screen anyway.
        }
    }

    private void PersistQuickTemperatures() =>
        Persist(() => _settings.PredefinedTemperatures = QuickTemperatures.ToList());

    private void Persist(Action apply)
    {
        if (_loading) return;

        apply();
        Save();
    }

    private void Save() => _settingsService.Save(_settings);

    /// <summary>Re-reads every computed label. Called after a language change; passing a
    /// null property name is the framework's "all of them" signal.</summary>
    public void RefreshText() => OnPropertyChanged(new PropertyChangedEventArgs(null));
}

