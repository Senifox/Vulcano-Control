using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Services;

/// <summary>
/// Plays the app's two sounds on Windows through winmm's PlaySound, straight out of the embedded
/// WAVs. The alternative was a package for System.Media.SoundPlayer, which wraps this same call.
///
/// The sounds arrived as MP3 from the WPF version, where WPF's MediaPlayer decoded them; nothing in
/// Avalonia does, so they were converted to mono 16-bit WAV once - a second and a half of chime
/// each, about 290 KB for the pair, which is a fair price for having no audio dependency at all.
///
/// This is the Windows implementation. <see cref="ISoundPlayer"/> exists so that the BlueZ build can
/// bring its own (paplay or aplay) without anything above this line noticing.
/// </summary>
public sealed class WindowsSoundPlayer : ISoundPlayer, IDisposable
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(IntPtr data, IntPtr module, uint flags);

    private readonly Dictionary<AppSound, GCHandle> _sounds = new();
    private readonly LogService _log;

    public WindowsSoundPlayer(LogService log)
    {
        _log = log;

        Load(AppSound.HeatReached, "Heat-Reached.wav");
        Load(AppSound.Shutdown, "Shutdown.wav");
    }

    public void Play(AppSound sound)
    {
        if (!_sounds.TryGetValue(sound, out var handle)) return;

        // Asynchronous, so a chime never holds up the caller - which is a device notification
        // arriving on a background thread. NoDefault means a broken WAV stays silent instead of
        // producing the system beep.
        PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, SndAsync | SndMemory | SndNoDefault);
    }

    /// <summary>
    /// Reads one WAV into memory and pins it there for good. PlaySound with SND_ASYNC keeps reading
    /// from the buffer after it returns, so the bytes have to stay where they were handed over - and
    /// since both sounds live as long as the app does, pinning once beats pinning per play.
    /// </summary>
    private void Load(AppSound sound, string fileName)
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri($"avares://vulcano-control/Assets/Sounds/{fileName}"));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);

            _sounds[sound] = GCHandle.Alloc(memory.ToArray(), GCHandleType.Pinned);
        }
        catch (Exception ex)
        {
            _log.Log(Strings.Get("Log.Sound.LoadFailed", fileName, ex.Message), LogLevel.Warning);
        }
    }

    public void Dispose()
    {
        // Stop anything still playing before the buffers it is reading from are unpinned.
        PlaySound(IntPtr.Zero, IntPtr.Zero, SndAsync | SndMemory);

        foreach (var handle in _sounds.Values)
        {
            handle.Free();
        }

        _sounds.Clear();
    }
}
