using System.Globalization;
using System.Text;
using Vulcano.Core.Services;

namespace Vulcano.Measure;

/// <summary>
/// Writes every temperature the device reports to a CSV, tagged with the phase it belongs to and
/// what the device was being asked to do at the time.
///
/// Two kinds of row, told apart by the source column. A <c>notify</c> row is the device saying the
/// temperature changed - event-driven, so the row spacing is itself information about how fast
/// things are moving. A <c>tick</c> row repeats the last known value on a fixed beat, so that a
/// stretch where nothing changed is visible as data rather than as a gap, and so a curve fit has
/// evenly spaced samples to work with.
///
/// Written with invariant numbers and commas: it is meant to be parsed, not double-clicked. A German
/// Excel will need the import dialog rather than a straight open.
/// </summary>
public sealed class Recorder : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly DateTime _runStartedUtc = DateTime.UtcNow;

    private DateTime _phaseStartedUtc = DateTime.UtcNow;
    private string _phase = "-";
    private double _lastMeasured = double.NaN;
    private double _target = double.NaN;
    private bool _heater;
    private bool _pump;

    public Recorder(string path)
    {
        Path = path;
        _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine("utc,run_seconds,phase_seconds,phase,source,measured_c,target_c,heater,pump,note");
    }

    public string Path { get; }

    /// <summary>The most recent temperature the device reported, or NaN before the first one.</summary>
    public double LastMeasured
    {
        get { lock (_lock) { return _lastMeasured; } }
    }

    /// <summary>
    /// Whether the last reading can be believed. The device reports a single 0 right after
    /// connecting, before its first real measurement - and a Volcano standing in a room is never at
    /// 0 °C, so that value is an artefact of the first read rather than a temperature.
    ///
    /// It is still written to the file: the recording says what the device said, and a phase that
    /// starts by waiting for something plausible is a separate decision from what gets recorded.
    /// </summary>
    public bool HasPlausibleReading
    {
        get { lock (_lock) { return !double.IsNaN(_lastMeasured) && _lastMeasured > 5; } }
    }

    public bool HeaterOn
    {
        get { lock (_lock) { return _heater; } }
    }

    public void BeginPhase(string phase, string note = "")
    {
        lock (_lock)
        {
            _phase = phase;
            _phaseStartedUtc = DateTime.UtcNow;
        }

        Write("phase", note.Length > 0 ? note : $"phase {phase} begins");
    }

    public void SetTarget(double celsius)
    {
        lock (_lock) { _target = celsius; }
        Write("event", $"target set to {celsius.ToString("0.#", CultureInfo.InvariantCulture)}");
    }

    public void Note(string note) => Write("event", note);

    public void OnTemperature(double celsius)
    {
        lock (_lock) { _lastMeasured = celsius; }
        Write("notify");
    }

    public void OnActivity(bool heater, bool pump)
    {
        bool changed;
        lock (_lock)
        {
            changed = heater != _heater || pump != _pump;
            _heater = heater;
            _pump = pump;
        }

        if (changed) Write("event", $"heater {(heater ? "on" : "off")}, pump {(pump ? "on" : "off")}");
    }

    /// <summary>Runs until cancelled, writing one repeat row every few seconds.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!double.IsNaN(LastMeasured)) Write("tick");
            }
        }
        catch (OperationCanceledException)
        {
            // Ending the run is not a failure.
        }
    }

    private void Write(string source, string note = "")
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _writer.WriteLine(string.Join(',',
                now.ToString("O", CultureInfo.InvariantCulture),
                (now - _runStartedUtc).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
                (now - _phaseStartedUtc).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
                _phase,
                source,
                Format(_lastMeasured),
                Format(_target),
                _heater ? "1" : "0",
                _pump ? "1" : "0",
                note.Replace(',', ';')));
        }
    }

    private static string Format(double value) =>
        double.IsNaN(value) ? "" : value.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose() => _writer.Dispose();
}
