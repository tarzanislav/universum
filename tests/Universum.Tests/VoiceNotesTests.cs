using Universum.Core.Speech;

namespace Universum.Tests;

/// <summary>Hlasové poznámky: the microphones FFmpeg lists, raw samples made a WAV file, the text of a transcription.</summary>
public sealed class VoiceNotesTests
{
    [Test]
    public void MicrophonesFromTheListOfFfmpeg()
    {
        const string output = """
            [dshow @ 000001] "Vymyslená kamera" (video)
            [dshow @ 000001]   Alternative name "@device_pnp_\\?\usb#vymyslena"
            [dshow @ 000001] "Mikrofón (Vymyslená karta)" (audio)
            [dshow @ 000001]   Alternative name "@device_cm_{vymyslene}"
            [dshow @ 000001] "Stereo Mix (Vymyslená karta)" (audio)
            dummy: Immediate exit requested
            """;
        Assert.True(VoiceNotes.Microphones(output).SequenceEqual(["Mikrofón (Vymyslená karta)", "Stereo Mix (Vymyslená karta)"]), "the audio devices, not the camera");
        Assert.True(VoiceNotes.Microphones("dummy: Immediate exit requested").Count == 0, "none");
    }

    [Test]
    public void RawSamplesBecomeAWave()
    {
        var folder = Directory.CreateTempSubdirectory("universum-voice-").FullName;
        try
        {
            var samples = Path.Combine(folder, "nahravka.pcm");
            File.WriteAllBytes(samples, new byte[VoiceNotes.SampleRate * 2 * 3 + 1]); // three seconds and half a sample (a recording stopped at any moment)
            var wave = Path.Combine(folder, "nahravka.wav");
            var length = VoiceNotes.Wave(samples, wave);
            var bytes = File.ReadAllBytes(wave);
            Assert.True(length == TimeSpan.FromSeconds(3) && bytes.Length == 44 + (VoiceNotes.SampleRate * 2 * 3), "three seconds, the half sample left out");
            Assert.True(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 8) == "WAVEfmt " &&
                        BitConverter.ToUInt32(bytes, 24) == VoiceNotes.SampleRate && BitConverter.ToUInt16(bytes, 22) == 1 && BitConverter.ToUInt32(bytes, 40) == VoiceNotes.SampleRate * 2 * 3,
                "a WAV header of 16 kHz mono");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void TextOfATranscription()
    {
        const string json = """
            { "transcription": [ { "text": " [BLANK_AUDIO]" }, { "text": " Vymyslená poznámka o filme." }, { "text": " (smiech) Zajtra ho spracujem. ♪" } ] }
            """;
        Assert.Equal("Vymyslená poznámka o filme. Zajtra ho spracujem.", VoiceNotes.Text(json));
        Assert.Equal("", VoiceNotes.Text("{ not json"));
    }
}
