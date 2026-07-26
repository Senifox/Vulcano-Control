using System.Globalization;
using Vulcano.Bluetooth.Windows;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Measure;

/// <summary>
/// Measures what a Volcano actually does, so the ramp editor can warn with numbers somebody read off
/// a device rather than numbers somebody assumed.
///
/// Three phases, each answering one question:
///
///   A  How fast does it heat when the target is far above it? The device's own controller drives
///      harder the bigger the gap, so this is the upper envelope - not what a ramp gets.
///   B  How fast does it cool with the heater off? There is no active cooling, and this is the one
///      figure we have nothing for at all. The long phase.
///   D  How fast does it heat under the way we actually drive a ramp - the target nudged along a
///      degree at a time? The gap between D and A is the price of that strategy, and if it is large
///      the answer is to push the target ahead on steep segments rather than to blame the device.
///
/// Cooling with the pump running was considered and dropped: it moves air through the filling
/// chamber, so it would only be usable if the chamber were taken off and put back for every run.
/// </summary>
internal static class Program
{
    private const double MaxTargetCelsius = 230;
    private const double ColdEnoughToStartCelsius = 40;
    /// <summary>
    /// Deliberately below 40. The device is suspected of reporting nothing once it drops under the
    /// temperature it is willing to display, which is around there - so the cooling phase is asked
    /// to walk past that point. If the readings stop while the tick rows keep repeating the last
    /// one, that is the answer, and the last real reading says where the threshold sits.
    /// </summary>
    private const double CoolDownFloorCelsius = 32;

    private static readonly TimeSpan HeatTimeout = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan CoolTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(90);

    /// <summary>Long enough that slow cooling near the end is not mistaken for silence: down there a
    /// tenth of a degree still takes a while, but not three minutes.</summary>
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromMinutes(3);

    private static async Task<int> Main(string[] args)
    {
        var phases = ReadPhases(args);
        var outputDirectory = ReadOutput(args);
        Directory.CreateDirectory(outputDirectory);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        var csvPath = Path.Combine(outputDirectory, $"vulcano-measure-{stamp}.csv");
        var logPath = Path.Combine(outputDirectory, $"vulcano-measure-{stamp}.log");

        Console.WriteLine("Vulcano measurement run");
        Console.WriteLine($"  phases : {string.Join(", ", phases)}");
        Console.WriteLine($"  data   : {csvPath}");
        Console.WriteLine();
        Console.WriteLine("Before starting, please make sure:");
        Console.WriteLine("  - nothing is on the device, no filling chamber attached");
        Console.WriteLine("  - it stands somewhere still - a draught mostly ruins the cooling curve");
        Console.WriteLine("  - it is cold, or phase A measures a climb that already had a head start");
        Console.WriteLine();

        using var recorder = new Recorder(csvPath);
        var log = new LogService(logPath);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("Stopping - switching the heater off before leaving.");
            cancellation.Cancel();
        };

        await using var device = new BluetoothVolcanoDevice(new WinRtVolcanoTransport(log), log);

        device.CurrentTemperatureChanged += (_, c) => recorder.OnTemperature(c);
        device.ActivityChanged += (_, activity) => recorder.OnActivity(
            (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0,
            (activity & VolcanoUuids.ActivityFlags.PumpEnabled) != 0);

        Console.Write("Connecting... ");
        if (!await device.ScanAndConnectAsync(cancellation.Token))
        {
            Console.WriteLine("no device found. Is it switched on and in range?");
            return 1;
        }
        Console.WriteLine("connected.");

        // The device reports on change, so a run that starts before the first report has no reading
        // to write down. Worth waiting for rather than recording a NaN as the starting point.
        await WaitForFirstReadingAsync(recorder, cancellation.Token);

        if (recorder.HasPlausibleReading)
        {
            Console.WriteLine($"Starting temperature: {recorder.LastMeasured:0.#} °C");

            if (recorder.LastMeasured > ColdEnoughToStartCelsius && phases.Contains("A"))
            {
                Console.WriteLine();
                Console.WriteLine($"That is above {ColdEnoughToStartCelsius:0} °C. Phase A is meant to start cold;");
                Console.WriteLine("letting it cool first gives a curve that can be compared with later runs.");
            }
        }
        else
        {
            // Not a fault and not a reason to stop: a Volcano that has not heated since it was
            // switched on answers the temperature read with zero and notifies nothing, because
            // notifications follow changes. The first real reading arrives once phase A switches the
            // heater on, which is a second or two away.
            recorder.Note("no temperature before the heater came on - device idle since power-up");
            Console.WriteLine("No temperature yet - the device reports one once it starts heating.");
        }

        // This switches a kilowatt heater on. With input redirected - run from a script, a task
        // runner, anything that is not a person at a terminal - ReadLine returns immediately and the
        // confirmation below would be no confirmation at all, so it has to be asked for explicitly.
        if (Console.IsInputRedirected && !args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine("Input is redirected, so there is nobody here to confirm.");
            Console.WriteLine("Run this from a terminal, or pass --yes if you meant it.");
            await device.DisconnectAsync();
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("Press Enter to begin, Ctrl+C to stop at any point.");
        if (!Console.IsInputRedirected) Console.ReadLine();

        var ticking = recorder.TickAsync(cancellation.Token);

        try
        {
            if (phases.Contains("A")) await RunHeatToMaxAsync(device, recorder, cancellation.Token);
            if (phases.Contains("B")) await RunPassiveCoolingAsync(device, recorder, cancellation.Token);
            if (phases.Contains("D")) await RunFollowRateAsync(device, recorder, log, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelled.");
        }
        finally
        {
            // Whatever happened, the device does not stay heating because a measurement ended.
            recorder.Note("run finished, switching the heater off");
            try { await device.SetHeaterAsync(false); } catch { /* best-effort */ }
            try { await device.SetPumpAsync(false); } catch { /* best-effort */ }

            cancellation.Cancel();
            await ticking;
            await device.DisconnectAsync();
        }

        Console.WriteLine();
        Console.WriteLine($"Done. Data: {csvPath}");
        return 0;
    }

    // --- Phase A ---

    /// <summary>
    /// Target straight to the top and let the device's own controller do its worst. What comes out is
    /// the heating rate as a function of temperature at full drive, which is the ceiling any ramp is
    /// measured against.
    /// </summary>
    private static async Task RunHeatToMaxAsync(IVolcanoDevice device, Recorder recorder, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"Phase A - heating to {MaxTargetCelsius:0} °C at full drive. A couple of minutes.");

        recorder.BeginPhase("A", $"heat to {MaxTargetCelsius:0} with a large delta");
        await device.SetTargetTemperatureAsync(MaxTargetCelsius);
        recorder.SetTarget(MaxTargetCelsius);
        await device.SetHeaterAsync(true);

        var started = DateTime.UtcNow;
        var best = recorder.LastMeasured;
        var bestAt = started;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var now = recorder.LastMeasured;

            // The device shuts its own heater off after a few minutes. A ramp switches it back on and
            // so does this, or the phase would measure the auto shut-off rather than the heater.
            if (!recorder.HeaterOn)
            {
                recorder.Note("heater had gone off by itself - switching it back on");
                await device.SetHeaterAsync(true);
            }

            if (now >= MaxTargetCelsius - 1)
            {
                recorder.Note("target reached");
                Console.WriteLine($"  reached {now:0.#} °C after {(DateTime.UtcNow - started).TotalSeconds:0} s");
                return;
            }

            if (now > best + 0.5)
            {
                best = now;
                bestAt = DateTime.UtcNow;
                Console.Write($"\r  {now:0.#} °C after {(DateTime.UtcNow - started).TotalSeconds:0} s      ");
            }
            else if (DateTime.UtcNow - bestAt > StallWindow)
            {
                recorder.Note($"stopped rising at {now:0.#}");
                Console.WriteLine();
                Console.WriteLine($"  stopped rising at {now:0.#} °C - as far as it goes.");
                return;
            }

            if (DateTime.UtcNow - started > HeatTimeout)
            {
                recorder.Note("phase A timed out");
                Console.WriteLine();
                Console.WriteLine("  timed out.");
                return;
            }
        }
    }

    // --- Phase B ---

    private static async Task RunPassiveCoolingAsync(IVolcanoDevice device, Recorder recorder, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"Phase B - heater off, cooling freely to {CoolDownFloorCelsius:0} °C.");
        Console.WriteLine("  This is the long one; up to 45 minutes. Leave the device alone.");

        recorder.BeginPhase("B", "passive cooling, heater and pump off");
        await device.SetHeaterAsync(false);
        await device.SetPumpAsync(false);
        recorder.SetTarget(double.NaN);

        var started = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            var now = recorder.LastMeasured;
            var elapsed = DateTime.UtcNow - started;
            Console.Write($"\r  {now:0.#} °C after {elapsed.TotalMinutes:0.0} min      ");

            if (now <= CoolDownFloorCelsius)
            {
                recorder.Note($"reached {CoolDownFloorCelsius:0}");
                Console.WriteLine();
                Console.WriteLine($"  down to {now:0.#} °C after {elapsed.TotalMinutes:0.0} min.");
                return;
            }

            // Going quiet is a result, not a stall - the suspicion is that the device stops reporting
            // below the temperature it will display. Ending here records where that happened instead
            // of sitting out the timeout with a flat line.
            if (DateTime.UtcNow - recorder.LastNotifyUtc > SilenceWindow)
            {
                recorder.Note($"device stopped reporting at {now:0.#}");
                Console.WriteLine();
                Console.WriteLine($"  no new reading for {SilenceWindow.TotalMinutes:0} min - it went quiet at {now:0.#} °C.");
                Console.WriteLine("  That is the threshold question answered; noted in the data.");
                return;
            }

            if (elapsed > CoolTimeout)
            {
                recorder.Note("phase B timed out");
                Console.WriteLine();
                Console.WriteLine($"  still at {now:0.#} °C after {elapsed.TotalMinutes:0.0} min - stopping here.");
                return;
            }
        }
    }

    // --- Phase D ---

    /// <summary>
    /// Runs a real <see cref="RampSessionController"/> over a staircase of ever steeper segments -
    /// 2, 5, 10 and 20 K per minute - and records how far the measured temperature falls behind the
    /// plan. Deliberately the production controller rather than something written for the occasion:
    /// the question is what our own pacing achieves, and a reimplementation would answer a different
    /// question.
    /// </summary>
    private static async Task RunFollowRateAsync(
        IVolcanoDevice device,
        Recorder recorder,
        LogService log,
        CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("Phase D - following a ramp that gets steeper: 2, 5, 10, 20 K/min.");
        Console.WriteLine("  It warms up to 100 °C first; that part is not counted.");

        RampPoint[] staircase =
        [
            new(0, 100, CurveKind.Linear),
            new(2, 104, CurveKind.Linear),   //  2 K/min
            new(4, 114, CurveKind.Linear),   //  5 K/min
            new(6, 134, CurveKind.Linear),   // 10 K/min
            new(8, 174, CurveKind.Linear),   // 20 K/min
        ];

        if (!TemperatureRampPlan.TryCreate(staircase, TimeSpan.Zero, out var plan, out var errors))
        {
            Console.WriteLine($"  the staircase is not a valid ramp: {string.Join(", ", errors.Select(e => e.Issue))}");
            return;
        }

        recorder.BeginPhase("D", "following the ramp controller's own pacing");

        using var ramp = new RampSessionController(device, log);
        var finished = new TaskCompletionSource();

        ramp.ProgressChanged += (_, e) =>
        {
            // The plan's target, which is the thing the measured value is supposed to be tracking.
            recorder.SetTarget(Math.Round(e.CurrentComputedTarget, 2));
        };
        ramp.WarmupCompleted += (_, _) =>
        {
            recorder.Note("warm-up done, the staircase starts here");
            Console.WriteLine();
            Console.WriteLine("  warm-up done, staircase running.");
        };
        ramp.Completed += (_, _) => finished.TrySetResult();
        ramp.Stopped += (_, _) => finished.TrySetResult();
        ramp.ErrorOccurred += (_, message) =>
        {
            recorder.Note($"ramp error: {message}");
            finished.TrySetResult();
        };

        await ramp.StartAsync(plan!, heaterCurrentlyOn: recorder.HeaterOn);

        using var registration = ct.Register(() =>
        {
            ramp.Stop();
            finished.TrySetResult();
        });

        var progress = ReportProgressAsync(recorder, finished.Task, ct);
        await finished.Task;
        await progress;

        Console.WriteLine();
        Console.WriteLine("  staircase done.");
    }

    private static async Task ReportProgressAsync(Recorder recorder, Task until, CancellationToken ct)
    {
        while (!until.IsCompleted && !ct.IsCancellationRequested)
        {
            Console.Write($"\r  {recorder.LastMeasured:0.#} °C      ");
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
    }

    // --- Arguments and small helpers ---

    private static async Task WaitForFirstReadingAsync(Recorder recorder, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!recorder.HasPlausibleReading && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct);
        }
    }

    private static string[] ReadPhases(string[] args)
    {
        var value = ValueOf(args, "--phases");
        return value is null
            ? ["A", "B", "D"]
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(p => p.ToUpperInvariant())
                   .ToArray();
    }

    private static string ReadOutput(string[] args) =>
        ValueOf(args, "--out") ?? Path.Combine(AppPaths.DataDirectory, "measurements");

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
