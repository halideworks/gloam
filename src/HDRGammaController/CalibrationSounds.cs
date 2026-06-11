using System;
using System.IO;
using System.Media;

namespace HDRGammaController
{
    /// <summary>
    /// Synthesized UI sounds for calibration: a soft per-capture blip, a pleasant completion
    /// chime, and a low failure tone. Generated in-memory as 16-bit/44.1 kHz WAVs — no bundled
    /// assets, and independent of the Windows sound scheme (SystemSounds.* are silent when the
    /// scheme has no sound assigned, which is why "play sound on completion" did nothing).
    /// </summary>
    internal static class CalibrationSounds
    {
        private const int SampleRate = 44100;

        private static readonly Lazy<SoundPlayer> Capture = new(() =>
            CreatePlayer(Synthesize(0.09, (880, 0.0, 0.09, 0.16, 0.022))));

        // A major arpeggio (A5 → C#6 → E6), overlapping notes with a gentle decay.
        private static readonly Lazy<SoundPlayer> Completion = new(() =>
            CreatePlayer(Synthesize(0.85,
                (880.00, 0.00, 0.55, 0.16, 0.16),
                (1108.73, 0.16, 0.55, 0.16, 0.16),
                (1318.51, 0.32, 0.53, 0.18, 0.18))));

        // Two soft descending tones.
        private static readonly Lazy<SoundPlayer> Failure = new(() =>
            CreatePlayer(Synthesize(0.55,
                (440, 0.00, 0.25, 0.15, 0.10),
                (330, 0.22, 0.33, 0.15, 0.12))));

        /// <summary>
        /// Session-wide mute, toggled from the live calibration screen. Overrides every
        /// caller (capture, completion, failure, verify pass) without each having to check -
        /// the sounds may be welcome at the desk but not in a shared room.
        /// </summary>
        public static bool Muted { get; set; }

        public static void PlayCapture() => SafePlay(Capture);
        public static void PlayCompletion() => SafePlay(Completion);
        public static void PlayFailure() => SafePlay(Failure);

        private static void SafePlay(Lazy<SoundPlayer> player)
        {
            if (Muted) return;
            try { player.Value.Play(); } // async, fire-and-forget
            catch { /* sound is best-effort; never break calibration over it */ }
        }

        /// <summary>
        /// Renders overlapping sine notes: (frequency Hz, start s, duration s, amplitude 0..1,
        /// decay time-constant s). Each note gets a 4 ms attack ramp (no click) and an
        /// exponential decay; a touch of 2nd harmonic keeps it from sounding like a test tone.
        /// </summary>
        private static byte[] Synthesize(double totalSeconds, params (double Freq, double Start, double Dur, double Amp, double Tau)[] notes)
        {
            int samples = (int)(totalSeconds * SampleRate);
            var mix = new double[samples];
            foreach (var n in notes)
            {
                int start = (int)(n.Start * SampleRate);
                int len = Math.Min((int)(n.Dur * SampleRate), samples - start);
                for (int i = 0; i < len; i++)
                {
                    double t = i / (double)SampleRate;
                    double attack = Math.Min(1.0, t / 0.004);
                    double envelope = attack * Math.Exp(-t / n.Tau);
                    double w = 2 * Math.PI * n.Freq * t;
                    mix[start + i] += n.Amp * envelope * (Math.Sin(w) + 0.25 * Math.Sin(2 * w));
                }
            }

            var pcm = new short[samples];
            for (int i = 0; i < samples; i++)
                pcm[i] = (short)(Math.Clamp(mix[i], -1.0, 1.0) * short.MaxValue);
            return WrapWav(pcm);
        }

        private static byte[] WrapWav(short[] pcm)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int dataBytes = pcm.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); // PCM, mono
            w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(dataBytes);
            foreach (short s in pcm) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }

        private static SoundPlayer CreatePlayer(byte[] wav)
        {
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Load();
            return player;
        }
    }
}
