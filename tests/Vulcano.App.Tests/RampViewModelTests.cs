using Avalonia.Headless.XUnit;
using Vulcano.App.ViewModels;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Tests;

/// <summary>
/// The ramp editor's own logic, away from the window.
///
/// Every test runs as an <see cref="AvaloniaFactAttribute"/> because the view models marshal device
/// events through the dispatcher; without one running, a posted job never happens and the assertion
/// races the framework rather than the code.
///
/// The settings file is a temporary one - these tests must never be able to touch the settings of
/// somebody who happens to have the app installed on the machine running them.
/// </summary>
public sealed class RampViewModelTests : IDisposable
{
    private readonly string _settingsFile =
        Path.Combine(Path.GetTempPath(), $"vulcano-vm-{Guid.NewGuid():N}.json");
    private readonly string _logFile =
        Path.Combine(Path.GetTempPath(), $"vulcano-vm-{Guid.NewGuid():N}.log");

    private readonly FakeVolcanoDevice _device = new();
    private readonly LogService _log;
    private readonly RampSessionController _ramp;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;

    public RampViewModelTests()
    {
        _log = new LogService(_logFile);
        _ramp = new RampSessionController(_device, _log, TimeSpan.FromMilliseconds(25));
        _settingsService = new SettingsService(_settingsFile, []);

        _settings = new AppSettings
        {
            RampProfiles =
            [
                new RampProfile
                {
                    Name = "Evening",
                    Points = [new RampPoint(0, 185, CurveKind.Linear), new RampPoint(20, 205, CurveKind.Linear)],
                    HoldMinutes = 5,
                },
            ],
            ActiveRampProfileName = "Evening",
        };
    }

    private RampViewModel Create() => new(_device, _ramp, _settingsService, _settings);

    public void Dispose()
    {
        _ramp.Dispose();
        foreach (var file in new[] { _settingsFile, _logFile })
        {
            try { File.Delete(file); } catch { /* best-effort */ }
        }
    }

    [AvaloniaFact]
    public void The_active_profile_is_loaded_into_the_editor()
    {
        var vm = Create();

        Assert.Equal("Evening", vm.SelectedProfile?.Name);
        Assert.Equal("Evening", vm.ProfileName);
        Assert.Equal(2, vm.Points.Count);
        Assert.Equal(5, vm.HoldMinutes);
        Assert.True(vm.IsValid);
    }

    /// <summary>
    /// Numbering is not decoration: the segment label reads it, and a freshly loaded profile once
    /// announced "SEGMENT 0 to 1" because the labels were asked before the numbers were assigned.
    /// </summary>
    [AvaloniaFact]
    public void Points_are_numbered_from_one_and_the_segment_label_agrees()
    {
        var vm = Create();

        Assert.Equal([1, 2], vm.Points.Select(p => p.Number));
        Assert.Contains("1", vm.SegmentTitle);
        Assert.Contains("2", vm.SegmentTitle);
    }

    /// <summary>
    /// The bug this project exists for. A point used to be inserted straight into the collection by
    /// the curve editor, so it was never subscribed to, never numbered, and editing it afterwards
    /// changed nothing the view model noticed.
    /// </summary>
    [AvaloniaFact]
    public void A_point_inserted_where_the_curve_was_clicked_is_numbered_and_watched()
    {
        var vm = Create();

        vm.InsertPointAtMinuteCommand.Execute(10);

        Assert.Equal(3, vm.Points.Count);
        Assert.Equal([1, 2, 3], vm.Points.Select(p => p.Number));
        Assert.Equal(10, vm.Points[1].TimeMinutes);

        // Watched: changing it has to reach the validation and the plan, with nothing else prodding
        // the view model.
        vm.Points[1].Celsius = 500;

        Assert.False(vm.IsValid);
        Assert.NotEqual("", vm.ValidationMessage);
    }

    [AvaloniaFact]
    public void An_edited_point_changes_what_the_device_would_be_asked_to_do()
    {
        var vm = Create();
        vm.InsertPointAtMinuteCommand.Execute(10);

        vm.Points[1].Celsius = 200;

        Assert.Equal(200, vm.Plan!.GetTargetTemperature(TimeSpan.FromMinutes(10)));
    }

    /// <summary>
    /// Editing a point has to reach the feasibility warning too - that is the path that stayed
    /// silent about a segment asking the device to shed 190 K in a minute.
    /// </summary>
    [AvaloniaFact]
    public void A_segment_the_device_cannot_follow_is_reported_without_anything_being_saved()
    {
        var vm = Create();
        Assert.Equal("", vm.ReachabilityNote);

        vm.InsertPointAtMinuteCommand.Execute(1);
        vm.Points[1].Celsius = 40;

        Assert.Contains("1", vm.ReachabilityNote);
        Assert.NotEqual("", vm.ReachabilityNote);
    }

    [AvaloniaFact]
    public void A_ramp_the_device_can_follow_says_nothing()
    {
        var vm = Create();

        Assert.Equal("", vm.ReachabilityNote);
    }

    // --- Profiles ---

    [AvaloniaFact]
    public void A_new_profile_copies_the_current_one_and_is_selected()
    {
        var vm = Create();

        vm.NewProfileCommand.Execute(null);

        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal("Evening 2", vm.SelectedProfile?.Name);
        Assert.Equal(2, vm.Points.Count);
    }

    [AvaloniaFact]
    public void Renaming_goes_through_saving_and_reaches_the_list()
    {
        var vm = Create();

        vm.ProfileName = "Late evening";
        vm.SaveProfileCommand.Execute(null);

        Assert.Equal("Late evening", vm.SelectedProfile?.Name);
        Assert.Equal("Late evening", vm.Profiles[0].Name);
        Assert.Equal("Late evening", _settingsService.Load().ActiveRampProfileName);
    }

    [AvaloniaFact]
    public void A_name_another_profile_holds_is_refused_and_says_so()
    {
        var vm = Create();
        vm.NewProfileCommand.Execute(null);

        vm.ProfileName = "Evening";
        vm.SaveProfileCommand.Execute(null);

        Assert.Equal("Evening 2", vm.SelectedProfile?.Name);
        Assert.NotEqual("", vm.ProfileMessage);
    }

    [AvaloniaFact]
    public void The_only_profile_cannot_be_deleted()
    {
        var vm = Create();

        Assert.False(vm.DeleteProfileCommand.CanExecute(null));

        vm.NewProfileCommand.Execute(null);
        Assert.True(vm.DeleteProfileCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Deleting_selects_a_neighbour_and_persists()
    {
        var vm = Create();
        vm.NewProfileCommand.Execute(null);

        vm.DeleteProfileCommand.Execute(null);

        Assert.Single(vm.Profiles);
        Assert.Equal("Evening", vm.SelectedProfile?.Name);
        Assert.Single(_settingsService.Load().RampProfiles);
    }

    // --- Starting ---

    [AvaloniaFact]
    public void A_ramp_cannot_be_started_while_the_device_is_away()
    {
        var vm = Create();

        Assert.False(vm.StartRampCommand.CanExecute(null));

        vm.IsConnected = true;
        Assert.True(vm.StartRampCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Starting_writes_the_first_point_to_the_device()
    {
        var vm = Create();
        vm.IsConnected = true;

        vm.StartRampCommand.Execute(null);

        await Wait.ForAsync(() => _device.WrittenTargets.Count > 0, "the first target to be written");
        Assert.Equal(185, _device.WrittenTargets[0]);
    }
}
