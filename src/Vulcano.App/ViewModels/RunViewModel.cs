using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>One block of the strip that runs across the whole ramp: warm-up, each segment, the hold.</summary>
public partial class RunSegmentViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isComplete;

    public RunSegmentViewModel(string label, double weight)
    {
        Label = label;
        Weight = weight;
    }

    public string Label { get; }

    /// <summary>Relative width, so a 20-minute segment looks twice as long as a 10-minute one.</summary>
    public double Weight { get; }
}

/// <summary>
/// What a running ramp looks like while it runs: how long is left, what the plan wants right now,
/// which segment it is in, and the three things worth doing to it - pause, skip, stop.
/// </summary>
public partial class RunViewModel : ObservableObject, IDisposable
{
    private readonly VolcanoDeviceOrchestrator _device;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasuredText))]
    [NotifyPropertyChangedFor(nameof(DeltaText))]
    private double _measured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanNowText))]
    [NotifyPropertyChangedFor(nameof(DeltaText))]
    private double _planNow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeLeftText))]
    [NotifyPropertyChangedFor(nameof(EndsAtText))]
    private TimeSpan _remaining;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentText))]
    [NotifyPropertyChangedFor(nameof(PhaseText))]
    private int _segmentNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentText))]
    private int _segmentCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseText))]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    private bool _isPaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseText))]
    private bool _isWarmingUp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseText))]
    private bool _isHolding;

    [ObservableProperty]
    private bool _isHeaterOn;

    [ObservableProperty]
    private bool _isPumpOn;

    [ObservableProperty]
    private string _segmentDetail = "";

    public RunViewModel(VolcanoDeviceOrchestrator device)
    {
        _device = device;

        _device.ProgressChanged += OnProgressChanged;
        _device.CurrentTemperatureChanged += OnCurrentTemperatureChanged;
        _device.ActivityChanged += OnActivityChanged;
    }

    public ObservableCollection<RunSegmentViewModel> Segments { get; } = new();

    public string MeasuredText => Math.Round(Measured).ToString("0");

    public string PlanNowText => Math.Round(PlanNow).ToString("0");

    public string DeltaText => Formatting.Kelvin(Measured - PlanNow);

    public string TimeLeftText => Formatting.Duration(Remaining);

    public string EndsAtText => Strings.Get("Run.EndsAt", (DateTime.Now + Remaining).ToString("HH:mm"));

    public string SegmentText => SegmentCount > 0 ? $"{SegmentNumber}/{SegmentCount}" : "—";

    public string PhaseText => Strings.Get(
        IsPaused ? "State.Paused"
        : IsWarmingUp ? "State.WarmingUp"
        : IsHolding ? "State.Holding"
        : "State.Running");

    public string PauseLabel => Strings.Get(IsPaused ? "Action.Resume" : "Action.Pause");

    [RelayCommand]
    private void TogglePause()
    {
        if (IsPaused)
        {
            _device.Resume();
        }
        else
        {
            _device.Pause();
        }
    }

    [RelayCommand]
    private void SkipSegment() => _device.SkipSegment();

    [RelayCommand]
    private void StopRamp() => _device.Stop();

    /// <summary>
    /// Builds the strip once per run, from the plan the controller is actually driving. A relay
    /// client has no plan to build it from, so it gets just the segments the progress events
    /// mention - which is enough for "2 of 3".
    /// </summary>
    private void RebuildSegments(RampProgressEventArgs progress)
    {
        Segments.Clear();

        if (_device.ActivePlan is not { } plan)
        {
            for (var i = 0; i < progress.SegmentCount; i++)
            {
                Segments.Add(new RunSegmentViewModel($"{i + 1}", 1));
            }
            return;
        }

        Segments.Add(new RunSegmentViewModel(Strings.Get("Run.WarmUp"), 0.6));

        for (var i = 0; i < plan.SegmentCount; i++)
        {
            var from = plan.Points[i];
            var to = plan.Points[i + 1];
            Segments.Add(new RunSegmentViewModel(
                $"{i + 1} · {CurveNames.Of(from.CurveToNext)}",
                Math.Max(to.TimeMinutes - from.TimeMinutes, 1)));
        }

        if (plan.HoldDuration > TimeSpan.Zero)
        {
            Segments.Add(new RunSegmentViewModel(
                Strings.Get("Run.HoldFor", Formatting.Minutes((int)plan.HoldDuration.TotalMinutes)),
                Math.Max(plan.HoldDuration.TotalMinutes, 1)));
        }
    }

    private void UpdateSegmentStates(RampProgressEventArgs progress)
    {
        if (Segments.Count == 0) return;

        var hasWarmUp = Segments[0].Label == Strings.Get("Run.WarmUp");
        var offset = hasWarmUp ? 1 : 0;

        var active = progress.IsWarmingUp && hasWarmUp
            ? 0
            : progress.IsHolding
                ? Segments.Count - 1
                : progress.SegmentIndex + offset;

        for (var i = 0; i < Segments.Count; i++)
        {
            Segments[i].IsActive = i == active;
            Segments[i].IsComplete = i < active;
        }
    }

    private void OnProgressChanged(object? sender, RampProgressEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            PlanNow = e.CurrentComputedTarget;
            Remaining = e.Remaining;
            IsPaused = e.IsPaused;
            IsWarmingUp = e.IsWarmingUp;
            IsHolding = e.IsHolding;
            SegmentCount = e.SegmentCount;
            SegmentNumber = Math.Min(e.SegmentIndex + 1, Math.Max(e.SegmentCount, 1));

            // The strip only has to be rebuilt when the shape of the run changes, which in practice
            // means once, at the start.
            if (Segments.Count == 0 ||
                (_device.ActivePlan is { } plan && Segments.Count != ExpectedSegmentCount(plan)))
            {
                RebuildSegments(e);
            }

            UpdateSegmentStates(e);
            UpdateSegmentDetail();
        });

    private static int ExpectedSegmentCount(TemperatureRampPlan plan) =>
        1 + plan.SegmentCount + (plan.HoldDuration > TimeSpan.Zero ? 1 : 0);

    private void UpdateSegmentDetail()
    {
        if (_device.ActivePlan is not { } plan || SegmentNumber < 1 || SegmentNumber > plan.SegmentCount)
        {
            SegmentDetail = "";
            return;
        }

        var from = plan.Points[SegmentNumber - 1];
        var to = plan.Points[SegmentNumber];
        SegmentDetail = $"{CurveNames.Of(from.CurveToNext)} → {Formatting.Celsius(to.Celsius)}";
    }

    private void OnCurrentTemperatureChanged(object? sender, double celsius) =>
        Dispatcher.UIThread.Post(() => Measured = celsius);

    private void OnActivityChanged(object? sender, ushort activity) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsHeaterOn = (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;
            IsPumpOn = (activity & VolcanoUuids.ActivityFlags.PumpEnabled) != 0;
        });

    /// <summary>Clears the strip so the next run builds its own.</summary>
    public void Reset() => Dispatcher.UIThread.Post(Segments.Clear);

    public void Dispose()
    {
        _device.ProgressChanged -= OnProgressChanged;
        _device.CurrentTemperatureChanged -= OnCurrentTemperatureChanged;
        _device.ActivityChanged -= OnActivityChanged;
    }

    /// <summary>Re-reads every computed label. Called after a language change; passing a
    /// null property name is the framework's "all of them" signal.</summary>
    public void RefreshText() => OnPropertyChanged(new PropertyChangedEventArgs(null));
}

