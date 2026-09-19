using System;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Runtime sound synthesis so the game never depends on audio files.
    ///
    /// Design rule: NO music. Everything here is diegetic - it models a physical event in the player's room
    /// (a goose's vocal tract, wings pushing air, webbed feet on the floor, a shell cracking, the player's own
    /// breath and pulse). Nothing is a UI jingle or a "video-game" sound.
    ///
    /// Honks use a tiny vocal-tract model: a raspy sawtooth "glottal" source with a pitch contour and a
    /// noise-jittered throat rattle, pushed through four resonant band-pass formants that open slightly over the
    /// syllable. That nasal formant stack is what makes it read as a goose instead of a buzzer.
    ///
    /// Foley is built by summing physically-motivated layers (decaying sines, filtered noise bursts, resonant
    /// clicks) into one buffer, then normalising to an authored peak so the relative loudness between clips is a
    /// design decision rather than an accident. All clips are mono at <see cref="SampleRate"/>. Generation is
    /// deterministic per seed, allocation-light (one buffer + one RNG per clip) and meant to run once at startup.
    /// </summary>
    public static class ProceduralAudio
    {
        public const int SampleRate = 44100;
        const float TwoPi = 2f * Mathf.PI;
        const float InvSR = 1f / SampleRate;

        static int Samples(float seconds) => Mathf.RoundToInt(seconds * SampleRate);

        // ------------------------------------------------------------------ filters
        /// <summary>
        /// RBJ biquad in transposed direct form II. The Set* methods update only the coefficients and leave the
        /// delay state (z1/z2) intact, so a filter can be swept smoothly without the zipper buzz that re-creating
        /// the struct (and zeroing its state) every few samples produced.
        /// </summary>
        struct Biquad
        {
            float b0, b1, b2, a1, a2, z1, z2;

            static void Prep(float fc, float q, out float alpha, out float cos0)
            {
                float w0 = TwoPi * Mathf.Clamp(fc, 20f, SampleRate * 0.45f) * InvSR;
                alpha = Mathf.Sin(w0) / (2f * Mathf.Max(q, 0.05f));
                cos0 = Mathf.Cos(w0);
            }

            /// <summary>Constant 0 dB peak-gain band-pass. Coefficients only; state is preserved.</summary>
            public void SetBandPass(float fc, float q)
            {
                Prep(fc, q, out float alpha, out float cos0);
                float inv = 1f / (1f + alpha);
                b0 = alpha * inv;
                b1 = 0f;
                b2 = -alpha * inv;
                a1 = -2f * cos0 * inv;
                a2 = (1f - alpha) * inv;
            }

            /// <summary>12 dB/oct low-pass. Coefficients only; state is preserved.</summary>
            public void SetLowPass(float fc, float q)
            {
                Prep(fc, q, out float alpha, out float cos0);
                float inv = 1f / (1f + alpha);
                float k = (1f - cos0) * inv;
                b0 = k * 0.5f;
                b1 = k;
                b2 = k * 0.5f;
                a1 = -2f * cos0 * inv;
                a2 = (1f - alpha) * inv;
            }

            /// <summary>12 dB/oct high-pass. Coefficients only; state is preserved.</summary>
            public void SetHighPass(float fc, float q)
            {
                Prep(fc, q, out float alpha, out float cos0);
                float inv = 1f / (1f + alpha);
                float k = (1f + cos0) * inv;
                b0 = k * 0.5f;
                b1 = -k;
                b2 = k * 0.5f;
                a1 = -2f * cos0 * inv;
                a2 = (1f - alpha) * inv;
            }

            public static Biquad BandPass(float fc, float q) { var f = new Biquad(); f.SetBandPass(fc, q); return f; }
            public static Biquad LowPass(float fc, float q) { var f = new Biquad(); f.SetLowPass(fc, q); return f; }
            public static Biquad HighPass(float fc, float q) { var f = new Biquad(); f.SetHighPass(fc, q); return f; }

            public float Process(float x)
            {
                float y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }

        /// <summary>One-pole (6 dB/oct) low-pass: the cheapest "muffle". Cascade two for a soft 12 dB/oct.</summary>
        struct OnePole
        {
            float a, z;

            public static OnePole LowPass(float fc) { var f = new OnePole(); f.SetCutoff(fc); return f; }

            /// <summary>Coefficient only; state is preserved so the cutoff can be swept.</summary>
            public void SetCutoff(float fc)
            {
                a = 1f - Mathf.Exp(-TwoPi * Mathf.Clamp(fc, 10f, SampleRate * 0.45f) * InvSR);
            }

            public float Process(float x)
            {
                z += a * (x - z);
                return z;
            }
        }

        /// <summary>
        /// Deterministic white-noise / random-parameter source. One per clip, seeded, so every variant of a sound
        /// is different but reproducible between runs.
        /// </summary>
        sealed class NoiseSource
        {
            readonly System.Random rng;
            public NoiseSource(int seed) { rng = new System.Random(seed); }

            /// <summary>White noise in [-1, 1].</summary>
            public float Next() { return (float)rng.NextDouble() * 2f - 1f; }

            /// <summary>Uniform float in [min, max).</summary>
            public float Range(float min, float max) { return min + (float)rng.NextDouble() * (max - min); }

            /// <summary>Uniform int in [min, maxExclusive).</summary>
            public int RangeInt(int min, int maxExclusive) { return rng.Next(min, maxExclusive); }
        }

        // ------------------------------------------------------------------ buffer helpers
        /// <summary>
        /// Sanitise, normalise to the authored peak <paramref name="gain"/> and wrap in a mono AudioClip.
        /// The gain is the per-clip loudness decision (footsteps quiet, impacts full scale) - see each factory.
        /// </summary>
        static AudioClip Make(string name, float[] data, float gain)
        {
            Clamp(data, 8f);
            Normalize(data, gain);
            var clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Guard: kill NaN/Inf and clamp runaway values so one misbehaving layer cannot poison normalisation.</summary>
        static void Clamp(float[] data, float limit)
        {
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
                data[i] = Mathf.Clamp(v, -limit, limit);
            }
        }

        static void Normalize(float[] data, float peak)
        {
            float max = 0f;
            for (int i = 0; i < data.Length; i++) max = Mathf.Max(max, Mathf.Abs(data[i]));
            if (max < 1e-5f) return;
            float g = peak / max;
            for (int i = 0; i < data.Length; i++) data[i] *= g;
        }

        /// <summary>Smoothstep attack / release window over [0, duration].</summary>
        static float Env(float t, float duration, float attack, float release)
        {
            float a = attack > 0f ? Mathf.Clamp01(t / attack) : 1f;
            float r = release > 0f ? Mathf.Clamp01((duration - t) / release) : 1f;
            a = a * a * (3f - 2f * a);
            r = r * r * (3f - 2f * r);
            return Mathf.Min(a, r);
        }

        /// <summary>Linear fade over the first and last <paramref name="seconds"/> of the buffer (clean loop seams).</summary>
        static void FadeEdges(float[] data, float seconds)
        {
            int n = Mathf.Min(Samples(seconds), data.Length / 2);
            for (int i = 0; i < n; i++)
            {
                float g = (i + 1) / (float)(n + 1);
                data[i] *= g;
                data[data.Length - 1 - i] *= g;
            }
        }

        // ------------------------------------------------------------------ layer builders
        // Each Add* sums one physical layer into `data` starting at sample `start`. Exponentials are run as
        // per-sample multiplies (no Exp/Pow in the hot loops) and every layer fades its last few ms so a truncated
        // tail can never click.

        /// <summary>Decaying sine whose frequency glides from fStart to fEnd (glideRate, decayRate in 1/s). The body of every thud.</summary>
        static void AddDecaySine(float[] data, int start, float duration, float fStart, float fEnd, float glideRate, float decayRate, float amp)
        {
            int n = Samples(duration);
            float glideMul = Mathf.Exp(-glideRate * InvSR);
            float decayMul = Mathf.Exp(-decayRate * InvSR);
            float glide = 1f, decay = 1f, phase = 0f;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx >= data.Length) break;
                float endFade = Mathf.Clamp01((duration - i * InvSR) * 200f); // 5 ms
                data[idx] += Mathf.Sin(TwoPi * phase) * decay * endFade * amp; // emit first so the layer starts at exactly 0
                float f = fEnd + (fStart - fEnd) * glide;
                phase += f * InvSR;
                if (phase >= 1f) phase -= 1f;
                glide *= glideMul;
                decay *= decayMul;
            }
        }

        /// <summary>White noise through <paramref name="filter"/> under a smooth attack + exponential decay. Slaps, splats, scuffs, taps.</summary>
        static void AddNoiseBurst(float[] data, int start, float duration, NoiseSource noise, ref Biquad filter, float attack, float decayRate, float amp)
        {
            int n = Samples(duration);
            int attackN = Samples(attack);
            float decayMul = Mathf.Exp(-decayRate * InvSR);
            float decay = 1f;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx >= data.Length) break;
                float a = attackN > 0 && i < attackN ? i / (float)attackN : 1f;
                a = a * a * (3f - 2f * a);
                float endFade = Mathf.Clamp01((duration - i * InvSR) * 250f); // 4 ms
                data[idx] += filter.Process(noise.Next()) * a * decay * endFade * amp;
                decay *= decayMul;
            }
        }

        /// <summary>Short resonant click: a few ms of decaying noise rung through a narrow band-pass, then left to ring. Beak clacks, shell ticks, twig snaps.</summary>
        static void AddClick(float[] data, int start, float length, float freq, float q, NoiseSource noise, float amp)
        {
            int n = Samples(length);
            int tail = Samples(0.004f);
            var bp = Biquad.BandPass(freq, q);
            float decayMul = Mathf.Exp(-(6f / Mathf.Max(length, 0.001f)) * InvSR);
            float decay = 1f;
            for (int i = 0; i < n + tail; i++)
            {
                int idx = start + i;
                if (idx >= data.Length) break;
                float x = i < n ? noise.Next() * decay : 0f;
                data[idx] += bp.Process(x) * amp;
                decay *= decayMul;
            }
        }

        /// <summary>Feather rustle: cubed (sparse, crackly) noise through a 2 kHz high-pass - feathers sliding over each other, not steady hiss.</summary>
        static void AddFeatherRustle(float[] data, int start, float duration, NoiseSource noise, float attack, float decayRate, float amp)
        {
            var hp = Biquad.HighPass(2000f, 0.7f);
            int n = Samples(duration);
            int attackN = Samples(attack);
            float decayMul = Mathf.Exp(-decayRate * InvSR);
            float decay = 1f;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx >= data.Length) break;
                float a = attackN > 0 && i < attackN ? i / (float)attackN : 1f;
                a = a * a * (3f - 2f * a);
                float x = noise.Next();
                x = x * x * x * 2.2f;
                float endFade = Mathf.Clamp01((duration - i * InvSR) * 250f);
                data[idx] += hp.Process(x) * a * decay * endFade * amp;
                decay *= decayMul;
            }
        }

        /// <summary>
        /// One wing stroke "whump": white noise through two cascaded one-poles whose cutoff opens from fcLow to
        /// fcTop as the wing accelerates, swelling over <paramref name="swell"/> seconds then decaying and darkening
        /// again as the wing slows. Optional very soft body thump underneath.
        /// </summary>
        static void AddWingStroke(float[] data, int start, float duration, NoiseSource noise, float amp, float swell, float decayRate, float fcLow, float fcTop, float thump)
        {
            int n = Samples(duration);
            var lp1 = OnePole.LowPass(fcLow);
            var lp2 = OnePole.LowPass(fcLow);
            float decayMul = Mathf.Exp(-decayRate * InvSR);
            float decay = 1f;
            swell = Mathf.Max(swell, 0.005f);
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx >= data.Length) break;
                float t = i * InvSR;
                float a = Mathf.Clamp01(t / swell);
                a = a * a * (3f - 2f * a);
                if (t > swell) decay *= decayMul;
                if ((i & 63) == 0)
                {
                    float fc = Mathf.Lerp(fcLow, fcTop, a * (0.35f + 0.65f * decay));
                    lp1.SetCutoff(fc);
                    lp2.SetCutoff(fc);
                }
                float v = lp2.Process(lp1.Process(noise.Next()));
                float endFade = Mathf.Clamp01((duration - t) * 200f);
                data[idx] += v * a * decay * endFade * 12f * amp;
            }
            if (thump > 0f) AddDecaySine(data, start, 0.12f, 95f, 70f, 40f, 25f, thump * amp);
        }

        /// <summary>
        /// Air whoosh: band-passed noise (Q 1.4) whose centre sweeps 300 Hz up to <paramref name="peakHz"/> at
        /// <paramref name="peakAt"/> (0..1 of the duration) then settles down to <paramref name="endHz"/>, with a
        /// little low-passed "air" body. Coefficients are refreshed every 64 samples without touching filter state.
        /// </summary>
        static void AddWhoosh(float[] data, int start, float duration, NoiseSource noise, float peakHz, float endHz, float peakAt, float amp)
        {
            int n = Samples(duration);
            var bp = Biquad.BandPass(300f, 1.4f);
            var body = OnePole.LowPass(400f);
            peakAt = Mathf.Clamp(peakAt, 0.1f, 0.9f);
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx >= data.Length) break;
                float t = i * InvSR;
                float k = t / duration;
                if ((i & 63) == 0)
                {
                    float centre;
                    if (k < peakAt)
                    {
                        float u = k / peakAt;
                        u = u * u * (3f - 2f * u);
                        centre = Mathf.Lerp(300f, peakHz, u);
                    }
                    else
                    {
                        float u = (k - peakAt) / (1f - peakAt);
                        u = u * u * (3f - 2f * u);
                        centre = Mathf.Lerp(peakHz, endHz, u);
                    }
                    bp.SetBandPass(centre, 1.4f);
                }
                float x = noise.Next();
                float env = Env(t, duration, 0.06f, 0.2f);
                data[idx] += (bp.Process(x) * 5f + body.Process(x) * 1.5f) * env * amp;
            }
        }

        /// <summary>Webbed foot hitting the floor: 80-110 Hz thud with a slight pitch drop + 500-900 Hz slap + brief 3-6 kHz scuff. Seeded variation on all three.</summary>
        static void AddFootstep(float[] data, int start, NoiseSource noise, float amp)
        {
            float thudHz = noise.Range(80f, 110f);
            AddDecaySine(data, start, 0.12f, thudHz * 1.3f, thudHz, 70f, 40f, 1.0f * amp);
            var slap = Biquad.BandPass(noise.Range(500f, 900f), 1.2f);
            AddNoiseBurst(data, start, noise.Range(0.025f, 0.035f), noise, ref slap, 0.002f, 110f, 5f * amp);
            var scuff = Biquad.BandPass(noise.Range(3000f, 6000f), 1.0f);
            AddNoiseBurst(data, start + Samples(0.003f), 0.015f, noise, ref scuff, 0.001f, 220f, noise.Range(1.2f, 3f) * amp);
        }

        /// <summary>One heart thump: 48 Hz body with an initial pitch bump + 120-180 Hz harmonic layer (so a phone speaker reproduces it) + tiny low-passed click.</summary>
        static void AddHeartThump(float[] data, int start, NoiseSource noise, float amp)
        {
            AddDecaySine(data, start, 0.28f, 48f * 1.6f, 48f, 40f, 22f, 1.0f * amp);
            AddDecaySine(data, start, 0.16f, 180f, 125f, 30f, 38f, 0.55f * amp);
            var lp = Biquad.LowPass(600f, 0.7f);
            AddNoiseBurst(data, start, 0.008f, noise, ref lp, 0.001f, 350f, 1.5f * amp);
        }

        // ------------------------------------------------------------------ vocal tract
        /// <summary>
        /// One goose syllable. f0 follows a fast rise then a fall ("hONNk"); four nasal formants open slightly
        /// (x0.92 -> x1.05) as the throat opens over the syllable; the rasp is an irregular 85-110 Hz glottal
        /// rattle whose rate and depth are jittered by slow noise (so it is a vibrating throat, not a tremolo);
        /// breath adds band-limited air that is strongest on the aspirated onset.
        /// </summary>
        static void Syllable(float[] data, int start, float duration, float f0Start, float f0Peak, float f0End,
            float amp, float rasp, float breath, float vibrato, int seed, float formantScale = 1f)
        {
            int n = Samples(duration);
            var noise = new NoiseSource(seed);
            var f1 = Biquad.BandPass(560f * formantScale, 7f);
            var f2 = Biquad.BandPass(1250f * formantScale, 9f);
            var f3 = Biquad.BandPass(2350f * formantScale, 10f);
            var f4 = Biquad.BandPass(3400f * formantScale, 8f);
            var breathBp = Biquad.BandPass(1800f, 1.2f);
            var wanderLp = OnePole.LowPass(22f);
            float raspHz = noise.Range(85f, 110f);
            float onsetMul = Mathf.Exp(-45f * InvSR);
            float onset = 1f;
            float phase = 0f, raspPhase = 0f;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx >= data.Length) break;
                float t = i * InvSR;
                float k = t / duration;
                // Formants open a little as the syllable progresses (coefficients only, state kept).
                if ((i & 63) == 0)
                {
                    float fs = formantScale * Mathf.Lerp(0.92f, 1.05f, k);
                    f1.SetBandPass(560f * fs, 7f);
                    f2.SetBandPass(1250f * fs, 9f);
                    f3.SetBandPass(2350f * fs, 10f);
                    f4.SetBandPass(3400f * fs, 8f);
                }
                float white = noise.Next();
                // Slow wander (+-1) that humanises pitch and drives the rasp irregularity.
                float wander = Mathf.Clamp(wanderLp.Process(white) * 28f, -1f, 1f);
                // Pitch contour: quick rise to the peak, then slide down, plus vibrato and a touch of jitter.
                float f0 = k < 0.18f ? Mathf.Lerp(f0Start, f0Peak, k / 0.18f) : Mathf.Lerp(f0Peak, f0End, (k - 0.18f) / 0.82f);
                f0 *= 1f + vibrato * Mathf.Sin(TwoPi * 7.5f * t + 1.3f) + 0.006f * wander;
                phase += f0 * InvSR;
                if (phase >= 1f) phase -= 1f;
                // Glottal-ish pulse: sharpened sawtooth.
                float saw = 2f * phase - 1f;
                float src = 0.6f * saw + 0.4f * saw * saw * saw;
                // Rasp: 85-110 Hz rattle with noise-modulated rate and depth, pulse-shaped rather than sinusoidal.
                raspPhase += raspHz * (1f + 0.08f * wander) * InvSR;
                if (raspPhase >= 1f) raspPhase -= 1f;
                float rp = 0.5f + 0.5f * Mathf.Sin(TwoPi * raspPhase);
                rp *= rp;
                src *= 1f - rasp * (0.75f + 0.25f * wander) * (1f - rp);
                // Breath: band-limited air, x3.5 on the "h" onset.
                src += breathBp.Process(white) * breath * (1f + 2.5f * onset);
                onset *= onsetMul;
                float v = f1.Process(src) * 1.0f + f2.Process(src) * 0.8f + f3.Process(src) * 0.55f + f4.Process(src) * 0.25f;
                v += src * 0.08f; // keep a little raw buzz
                data[idx] += v * Env(t, duration, 0.02f, 0.09f) * amp;
            }
        }

        // ------------------------------------------------------------------ honks (the goose's voice)
        /// <summary>Plain honk "hONK": one syllable from the goose's throat. Peak 0.9.</summary>
        public static AudioClip Honk(string name, float pitch = 1f, float duration = 0.42f, int seed = 1)
        {
            var data = new float[Samples(duration + 0.06f)];
            Syllable(data, 0, duration, 175f * pitch, 265f * pitch, 190f * pitch, 1f, 0.35f, 0.05f, 0.025f, seed);
            return Make(name, data, 0.9f);
        }

        /// <summary>Two-syllable angry honk "ha-HONK" repeated <paramref name="repeats"/> times, each a touch higher; seed adds pitch drift. Peak 0.9.</summary>
        public static AudioClip AngryHonk(string name, int repeats = 2, float pitch = 1f, int seed = 7)
        {
            repeats = Mathf.Max(1, repeats);
            float syll = 0.16f, main = 0.36f, gap = 0.05f, between = 0.11f;
            float total = repeats * (syll + gap + main + between);
            var data = new float[Samples(total + 0.05f)];
            var rnd = new NoiseSource(seed);
            int pos = 0;
            for (int r = 0; r < repeats; r++)
            {
                float p = pitch * (1f + r * 0.07f) * rnd.Range(0.97f, 1.03f);
                Syllable(data, pos, syll, 160f * p, 230f * p, 210f * p, 0.7f, 0.5f, 0.08f, 0.01f, seed + r * 3);
                pos += Samples(syll + gap);
                Syllable(data, pos, main, 230f * p, 310f * p, 205f * p, 1f, 0.55f, 0.06f, 0.03f, seed + r * 3 + 1);
                pos += Samples(main + between);
            }
            return Make(name, data, 0.9f);
        }

        /// <summary>Long, low, ominous honk from a big bird with its neck stretched out ("THE GOOSE KNOWS"). Peak 0.9.</summary>
        public static AudioClip DramaticHonk(string name)
        {
            float duration = 1.1f;
            var data = new float[Samples(duration + 0.1f)];
            Syllable(data, 0, duration, 120f, 200f, 105f, 1f, 0.45f, 0.1f, 0.035f, 42, 0.85f);
            return Make(name, data, 0.9f);
        }

        // ------------------------------------------------------------------ goose foley
        /// <summary>Webbed foot slapping the floor, ~0.14 s: thud + slap + scuff, all seed-varied. Peak 0.6 (quiet; they play constantly).</summary>
        public static AudioClip Footstep(string name, int seed)
        {
            var data = new float[Samples(0.14f)];
            var noise = new NoiseSource(seed);
            AddFootstep(data, 0, noise, 1f);
            return Make(name, data, 0.6f);
        }

        /// <summary>Single wing flap, ~0.34 s: downstroke whump (LP noise 120->450 Hz, 40 ms swell) + feather rustle 20 ms in + very soft 70 Hz thump. Peak 0.8.</summary>
        public static AudioClip Flap(string name, int seed)
        {
            var data = new float[Samples(0.34f)];
            var noise = new NoiseSource(seed);
            AddWingStroke(data, 0, 0.22f, noise, 1f, noise.Range(0.035f, 0.05f), 16f, 120f, noise.Range(400f, 520f), 0.35f);
            AddFeatherRustle(data, Samples(0.02f), 0.07f, noise, 0.008f, 60f, noise.Range(0.3f, 0.5f));
            return Make(name, data, 0.8f);
        }

        /// <summary>
        /// Seamless loop of exactly 0.34 s holding one full wing beat: downstroke whump at t=0, lighter/brighter
        /// upstroke rustle at t=0.19. Looped at pitch 1 that is ~3 beats/s; first/last 5 ms faded. Peak 0.7.
        /// </summary>
        public static AudioClip WingBeatLoop(string name)
        {
            const float period = 0.34f;
            var data = new float[Samples(period)];
            var noise = new NoiseSource(9001);
            // Downstroke: the big air displacement.
            AddWingStroke(data, 0, 0.22f, noise, 1f, 0.04f, 16f, 120f, 450f, 0.35f);
            AddFeatherRustle(data, Samples(0.02f), 0.07f, noise, 0.008f, 60f, 0.4f);
            // Upstroke: shorter, lighter, brighter, mostly feathers.
            AddWingStroke(data, Samples(0.19f), 0.12f, noise, 0.45f, 0.025f, 30f, 200f, 650f, 0f);
            AddFeatherRustle(data, Samples(0.2f), 0.06f, noise, 0.006f, 70f, 0.25f);
            FadeEdges(data, 0.005f);
            return Make(name, data, 0.7f);
        }

        /// <summary>Air whoosh, 0.5 s: band-passed noise sweeping 300 -> ~2800 Hz then back down a little (Q 1.4), 60 ms attack / 200 ms release; seed varies the sweep. Peak 0.8.</summary>
        public static AudioClip Whoosh(string name, int seed)
        {
            var data = new float[Samples(0.5f)];
            var noise = new NoiseSource(seed);
            AddWhoosh(data, 0, 0.5f, noise, noise.Range(2500f, 3100f), noise.Range(1500f, 2100f), noise.Range(0.5f, 0.68f), 1f);
            return Make(name, data, 0.8f);
        }

        /// <summary>Goose launching into a dash, 0.45 s: both feet pushing off 40 ms apart + a hard flap + the air whoosh of the take-off. Peak 0.9.</summary>
        public static AudioClip DashLaunch(string name, int seed)
        {
            var data = new float[Samples(0.45f)];
            var noise = new NoiseSource(seed);
            AddFootstep(data, 0, noise, 1f);
            AddFootstep(data, Samples(0.04f), noise, 0.85f);
            AddWingStroke(data, Samples(0.03f), 0.22f, noise, 1f, noise.Range(0.035f, 0.05f), 16f, 120f, noise.Range(420f, 520f), 0.35f);
            AddFeatherRustle(data, Samples(0.05f), 0.07f, noise, 0.008f, 60f, 0.4f);
            AddWhoosh(data, Samples(0.06f), 0.38f, noise, noise.Range(2300f, 3000f), noise.Range(1500f, 2000f), noise.Range(0.5f, 0.65f), 0.8f);
            return Make(name, data, 0.9f);
        }

        /// <summary>
        /// Being tackled by a goose, 0.7 s: 120 -> 55 Hz body thud + low-passed noise burst (the hit) + 250 ms of
        /// feathers everywhere + a secondary stumble bump + two woody beak clacks at t=0.12 and 0.21. Peak 1.0.
        /// </summary>
        public static AudioClip CatchImpact(string name)
        {
            var data = new float[Samples(0.7f)];
            var noise = new NoiseSource(77);
            AddDecaySine(data, 0, 0.45f, 120f, 55f, 12f, 9f, 1.0f);
            var hit = Biquad.LowPass(900f, 0.7f);
            AddNoiseBurst(data, 0, 0.16f, noise, ref hit, 0.003f, 30f, 5f);
            AddFeatherRustle(data, Samples(0.01f), 0.25f, noise, 0.015f, 14f, 0.45f);
            AddDecaySine(data, Samples(0.08f), 0.2f, 90f, 60f, 20f, 18f, 0.5f);
            AddClick(data, Samples(0.12f), 0.008f, 3100f, 8f, noise, 6f);
            AddClick(data, Samples(0.21f), 0.008f, 2700f, 8f, noise, 5f);
            return Make(name, data, 1.0f);
        }

        // ------------------------------------------------------------------ egg / nest foley
        /// <summary>
        /// Egg breaking on the floor, ~0.7 s: two ~3.2 kHz shell snaps + 700 Hz splat body + 140 Hz wobble, a wet
        /// 400 Hz splat layer, then 8-12 tiny shell fragments ticking (4-9 kHz) scattered over 0.15-0.55 s. Peak 0.95.
        /// </summary>
        public static AudioClip EggCrack(string name, int seed)
        {
            var data = new float[Samples(0.7f)];
            var noise = new NoiseSource(seed);
            float snapHz = noise.Range(3000f, 3500f);
            var snap1 = Biquad.BandPass(snapHz, 2.5f);
            AddNoiseBurst(data, 0, 0.03f, noise, ref snap1, 0.0005f, 220f, 6f);
            var snap2 = Biquad.BandPass(snapHz * 1.08f, 2.5f);
            AddNoiseBurst(data, Samples(0.035f), 0.03f, noise, ref snap2, 0.0005f, 260f, 4.8f);
            var splat = Biquad.LowPass(700f, 0.8f);
            AddNoiseBurst(data, Samples(0.05f), 0.4f, noise, ref splat, 0.004f, 9f, 2.2f);
            var wet = Biquad.LowPass(400f, 0.7f);
            AddNoiseBurst(data, Samples(0.04f), 0.12f, noise, ref wet, 0.006f, 25f, 5f);
            AddDecaySine(data, Samples(0.05f), 0.25f, 140f, 140f, 1f, 20f, 0.3f);
            int clicks = noise.RangeInt(8, 13);
            for (int c = 0; c < clicks; c++)
            {
                float at = noise.Range(0.15f, 0.55f);
                AddClick(data, Samples(at), noise.Range(0.003f, 0.006f), noise.Range(4000f, 9000f), noise.Range(5f, 9f), noise, noise.Range(2f, 4.5f));
            }
            return Make(name, data, 0.95f);
        }

        /// <summary>Picking an egg up, 0.3 s: hand/cloth rustle (800-3000 Hz band, low level) with a soft hollow shell tap (~1.2 kHz, Q 6) 50 ms in. Peak 0.5.</summary>
        public static AudioClip EggPickup(string name)
        {
            var data = new float[Samples(0.3f)];
            var noise = new NoiseSource(5150);
            var rustle = Biquad.BandPass(1550f, 0.7f);
            AddNoiseBurst(data, 0, 0.2f, noise, ref rustle, 0.03f, 12f, 0.5f);
            AddClick(data, Samples(0.05f), 0.02f, 1200f, 6f, noise, 5f);
            AddClick(data, Samples(0.05f), 0.02f, 700f, 4f, noise, 3f);
            return Make(name, data, 0.5f);
        }

        /// <summary>Setting something down in a nest, 0.6 s: soft 90 Hz thud at t=0, then 10-16 dry twig clicks (1.5-5 kHz) whose density decays. Peak 0.6.</summary>
        public static AudioClip NestPlace(string name)
        {
            var data = new float[Samples(0.6f)];
            var noise = new NoiseSource(2024);
            AddDecaySine(data, 0, 0.09f, 115f, 90f, 60f, 40f, 0.8f);
            var contact = Biquad.LowPass(500f, 0.7f);
            AddNoiseBurst(data, 0, 0.05f, noise, ref contact, 0.002f, 70f, 2.5f);
            int clicks = noise.RangeInt(10, 17);
            for (int c = 0; c < clicks; c++)
            {
                float u = noise.Range(0f, 1f);
                float at = 0.01f + 0.5f * u * u; // u^2 mapping: many clicks early, fewer as the twigs settle
                AddClick(data, Samples(at), noise.Range(0.004f, 0.01f), noise.Range(1500f, 5000f), noise.Range(5f, 9f), noise, noise.Range(2f, 5f) * (1f - 0.4f * u));
            }
            return Make(name, data, 0.6f);
        }

        // ------------------------------------------------------------------ player-side sounds
        /// <summary>Fingertip on paper/felt, 0.08 s: 1.8 kHz low-passed noise with a 12 ms attack and exponential decay + faint body. Peak 0.35 (the quietest thing in the game).</summary>
        public static AudioClip ButtonTap(string name)
        {
            var data = new float[Samples(0.08f)];
            var noise = new NoiseSource(42);
            var lp = Biquad.LowPass(1800f, 0.7f);
            AddNoiseBurst(data, 0, 0.08f, noise, ref lp, 0.012f, 60f, 1f);
            AddDecaySine(data, 0, 0.05f, 420f, 300f, 80f, 90f, 0.25f);
            return Make(name, data, 0.35f);
        }

        /// <summary>The player's own heartbeat, one "lub-dub" ~0.5 s: lub at t=0, softer dub at t=0.17, silent by 0.45 s. Peak 0.9.</summary>
        public static AudioClip HeartbeatOneShot(string name)
        {
            var data = new float[Samples(0.5f)];
            var noise = new NoiseSource(1999);
            AddHeartThump(data, 0, noise, 1.0f);
            AddHeartThump(data, Samples(0.17f), noise, 0.7f);
            return Make(name, data, 0.9f);
        }

        /// <summary>
        /// The player's breathing, 1.8 s seamless loop: inhale 0.7 s (band-passes ~700 Hz and ~1.8 kHz sweeping up),
        /// exhale 0.9 s (sweeping down, slightly louder, faint 90 Hz glottal rasp), 0.2 s gap; edges faded 10 ms. Peak 0.5.
        /// </summary>
        public static AudioClip BreathLoop(string name)
        {
            const float inhale = 0.7f, exhale = 0.9f, total = 1.8f;
            int n = Samples(total);
            var data = new float[n];
            var noise = new NoiseSource(311);
            var bpLow = Biquad.BandPass(600f, 2.2f);
            var bpHigh = Biquad.BandPass(1500f, 2.6f);
            var chest = OnePole.LowPass(260f);
            float raspPhase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i * InvSR;
                float env, lowC, highC, raspDepth = 0f;
                if (t < inhale)
                {
                    float ph = t / inhale;
                    env = Mathf.Sin(ph * Mathf.PI);
                    env = env * env * 0.8f;
                    lowC = Mathf.Lerp(600f, 850f, ph);
                    highC = Mathf.Lerp(1500f, 2300f, ph);
                }
                else if (t < inhale + exhale)
                {
                    float ph = (t - inhale) / exhale;
                    env = Mathf.Sin(ph * Mathf.PI);
                    env = env * env;
                    lowC = Mathf.Lerp(900f, 520f, ph);
                    highC = Mathf.Lerp(2200f, 1200f, ph);
                    raspDepth = 0.28f;
                }
                else
                {
                    env = 0f;
                    lowC = 520f;
                    highC = 1200f;
                }
                if ((i & 63) == 0)
                {
                    bpLow.SetBandPass(lowC, 2.2f);
                    bpHigh.SetBandPass(highC, 2.6f);
                }
                float x = noise.Next();
                float v = bpLow.Process(x) * 3.5f + bpHigh.Process(x) * 2.2f + chest.Process(x) * 2.5f;
                raspPhase += 90f * InvSR;
                if (raspPhase >= 1f) raspPhase -= 1f;
                float rasp = 1f - raspDepth * (0.5f + 0.5f * Mathf.Sin(TwoPi * raspPhase));
                data[i] = v * env * rasp;
            }
            FadeEdges(data, 0.01f);
            return Make(name, data, 0.5f);
        }
    }
}
