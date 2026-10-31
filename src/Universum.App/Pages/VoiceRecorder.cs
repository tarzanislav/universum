using System.IO;
using Universum.Core.Processes;
using Universum.Core.Speech;

namespace Universum.App.Pages;

/// <summary>
/// The voice of a note (owner, 31. 10. 2026): recorded from the first microphone by FFmpeg, kept as a WAV file in the agenda's
/// folder of recordings and written down by whisper.cpp – all in the computer, nothing sent anywhere.
/// </summary>
public sealed class VoiceRecorder(AppServices services)
{
    private CancellationTokenSource? _stop;
    private Task<ProcessResult>? _run;
    private string? _samples;

    /// <summary>The folder of the recordings of notes.</summary>
    public static string Folder => Path.Combine(AppSettings.Directory, "planovac", "nahravky");

    public bool IsRecording => _stop is not null;

    /// <summary>Starts recording; the problem in words, or null.</summary>
    public async Task<string?> StartAsync()
    {
        if (IsRecording)
        {
            return null;
        }

        try
        {
            var tools = await services.SpeechAsync();
            var devices = await services.Runner.RunAsync(VoiceNotes.DevicesRequest(tools));
            var microphones = VoiceNotes.Microphones(devices.StandardError + "\n" + devices.StandardOutput);
            if (microphones.Count == 0)
            {
                return "Nenašiel sa žiadny mikrofón – pripojte ho alebo ho povoľte v nastaveniach súkromia Windows (Mikrofón).";
            }

            Directory.CreateDirectory(Folder);
            _samples = Path.Combine(Folder, DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".pcm");
            _stop = new CancellationTokenSource();
            _run = services.Runner.RunAsync(VoiceNotes.RecordRequest(tools, microphones[0], _samples), _stop.Token);
            // a microphone that cannot be opened ends FFmpeg at once
            if (await Task.WhenAny(_run, Task.Delay(700)) == _run && _run.Result.ExitCode != 0)
            {
                var problem = _run.Result.StandardError.Trim();
                Reset();
                return "Mikrofón sa nedá otvoriť" + (problem.Length > 0 ? ": " + problem.Split('\n')[^1] : ".");
            }

            return null;
        }
        catch (Exception ex) when (ex is SpeechException or ProcessStartException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            Reset();
            return ex.Message;
        }
    }

    /// <summary>Stops recording and writes it down in the language: the recording's file name, its text and the problem.</summary>
    public async Task<(string? Recording, string? Text, string? Problem)> StopAsync(string language)
    {
        if (_stop is not { } stop || _run is not { } run || _samples is not { } samples)
        {
            return (null, null, null);
        }

        stop.Cancel();
        try
        {
            await run;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // stopped – the samples written so far are whole
        }

        Reset();
        if (!File.Exists(samples) || new FileInfo(samples).Length < VoiceNotes.SampleRate)
        {
            File.Delete(samples);
            return (null, null, "Nahrávka je prázdna alebo kratšia ako pol sekundy.");
        }

        var wave = Path.ChangeExtension(samples, ".wav");
        VoiceNotes.Wave(samples, wave);
        File.Delete(samples);
        var (text, problem) = await TranscribeAsync(wave, language);
        return (Path.GetFileName(wave), text, problem);
    }

    /// <summary>Writes down a WAV file (16 kHz mono) in the language.</summary>
    public async Task<(string? Text, string? Problem)> TranscribeAsync(string wave, string language)
    {
        try
        {
            return await VoiceNotes.TranscribeAsync(await services.SpeechAsync(), services.Runner, wave, language);
        }
        catch (Exception ex) when (ex is SpeechException or ProcessStartException or TimeoutException or IOException)
        {
            return (null, ex.Message);
        }
    }

    private void Reset()
    {
        _stop?.Dispose();
        _stop = null;
        _run = null;
    }
}
