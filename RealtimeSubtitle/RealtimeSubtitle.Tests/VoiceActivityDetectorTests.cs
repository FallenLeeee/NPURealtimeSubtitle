using RealtimeSubtitle.Core.Audio;

namespace RealtimeSubtitle.Tests;

public class VoiceActivityDetectorTests
{
    private static float[] Chunk(float value) => Enumerable.Repeat(value, 160).ToArray(); // 10 ms @ 16 kHz

    [Fact]
    public void Silence_StaysSilent()
    {
        var vad = new VoiceActivityDetector(threshold: 0.01f, minSpeechMs: 150, hangoverMs: 400);

        var result = vad.Process(Chunk(0f));
        Assert.Equal(VadState.Silence, result.State);
        Assert.False(result.Transition);
    }

    [Fact]
    public void SustainedSpeech_TransitionsOnceAfterMinDuration()
    {
        var vad = new VoiceActivityDetector(threshold: 0.01f, minSpeechMs: 150, hangoverMs: 400);
        int transitions = 0;
        VadState last = VadState.Silence;

        for (int i = 0; i < 20; i++) // 200 ms
        {
            var r = vad.Process(Chunk(0.4f));
            if (r.Transition) transitions++;
            last = r.State;
        }

        Assert.Equal(VadState.Speech, last);
        Assert.Equal(1, transitions);
    }

    [Fact]
    public void ShortBurst_BelowMinDuration_StaysSilent()
    {
        var vad = new VoiceActivityDetector(threshold: 0.01f, minSpeechMs: 150, hangoverMs: 400);

        var result = VadState.Silence;
        for (int i = 0; i < 5; i++) // 50 ms < 150 ms
        {
            result = vad.Process(Chunk(0.4f)).State;
        }

        Assert.Equal(VadState.Silence, result);
    }

    [Fact]
    public void Hangover_KeepsSpeechThroughGaps_ThenFallsBackToSilence()
    {
        var vad = new VoiceActivityDetector(threshold: 0.01f, minSpeechMs: 150, hangoverMs: 400);

        for (int i = 0; i < 20; i++) vad.Process(Chunk(0.4f)); // → Speech
        Assert.Equal(VadState.Speech, vad.State);

        // 200 ms of silence: still Speech thanks to hangover.
        for (int i = 0; i < 20; i++) vad.Process(Chunk(0f));
        Assert.Equal(VadState.Speech, vad.State);

        // 500 ms total → past 400 ms hangover → Silence.
        int transitions = 0;
        for (int i = 0; i < 50; i++)
        {
            if (vad.Process(Chunk(0f)).Transition) transitions++;
        }

        Assert.Equal(VadState.Silence, vad.State);
        Assert.Equal(1, transitions);
    }

    [Fact]
    public void Rms_IsCorrectlyComputed()
    {
        var vad = new VoiceActivityDetector();
        var result = vad.Process(Chunk(0.5f));
        Assert.InRange(Math.Abs(result.Rms - 0.5f), 0f, 0.001f);
    }
}