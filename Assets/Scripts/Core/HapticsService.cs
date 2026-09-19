using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Haptic feedback for the game. On a real iPhone this talks to the native bridge in
    /// Assets/Plugins/iOS/GooseHaptics.mm, which has two layers:
    /// <list type="bullet">
    ///   <item>Core Haptics (CHHapticEngine): transients with intensity + sharpness, continuous
    ///   buzzes with a fade-out and the authored <see cref="Pattern"/>s. Used whenever the device
    ///   supports it (<see cref="CoreHapticsAvailable"/>).</item>
    ///   <item>UIKit feedback generators (UIImpactFeedbackGenerator / UINotificationFeedbackGenerator):
    ///   the fallback when Core Haptics is unavailable or a native call threw. Every request is then
    ///   mapped to a Light / Medium / Heavy impact or a success / error notification.</item>
    /// </list>
    /// Android falls back to Handheld.Vibrate; the Editor only counts requests (<see cref="PulseCount"/>).
    ///
    /// Requests run through two independent rate limiters on Time.unscaledTime: the "event" channel
    /// (hits, catches, lunges, UI) and the "ambient" channel (heartbeat, footsteps, wing beats). An
    /// event never waits for the ambient channel and vice versa, so a background heartbeat cannot
    /// swallow a hit. The enabled flag is persisted in PlayerPrefs (<see cref="PrefsKey"/>).
    /// </summary>
    public class HapticsService : MonoBehaviour
    {
        public enum Strength { Light = 0, Medium = 1, Heavy = 2 }

        /// <summary>
        /// Authored Core Haptics patterns. The numeric order MUST match the switch in
        /// GooseHaptics.mm (BuildAuthoredPattern) - the enum value is passed as the native id.
        /// </summary>
        public enum Pattern
        {
            Heartbeat = 0,
            WingBeat,
            LungeWindup,
            LungeLaunch,
            Catch,
            EggCrack,
            RageRumble,
            Success,
            Landing,
            Footstep
        }

        /// <summary>PlayerPrefs key for the enabled flag (int 1/0, default 1).</summary>
        public const string PrefsKey = "GooseBrawl.Haptics";

        [Tooltip("Master switch. Loaded from PlayerPrefs in Awake; use SetEnabled() to change and persist it.")]
        public bool hapticsEnabled = true;

        [Tooltip("Minimum seconds between two haptics on the event channel (hits, catches, lunges, UI).")]
        public float eventMinInterval = 0.06f;

        [Tooltip("Minimum seconds between two haptics on the ambient channel (heartbeat, footsteps, wing beats).")]
        public float ambientMinInterval = 0.12f;

        /// <summary>The UIKit bridge answered the probe (iOS device builds only).</summary>
        public bool NativeAvailable { get; private set; }

        /// <summary>Core Haptics engine was created OK. Cleared at runtime after the first native failure.</summary>
        public bool CoreHapticsAvailable { get; private set; }

        /// <summary>Number of haptic requests accepted by the rate limiters. Also counts in the Editor.</summary>
        public int PulseCount { get; private set; }

        float m_NextEventAllowed;
        float m_NextAmbientAllowed;
        bool m_NotifyWarned;

#if UNITY_EDITOR
        static bool s_EditorNoticeLogged;
#endif

#if UNITY_IOS && !UNITY_EDITOR
        // UIKit layer (original bridge)
        [DllImport("__Internal")] static extern int GooseHaptics_IsSupported();
        [DllImport("__Internal")] static extern void GooseHaptics_Prepare();
        [DllImport("__Internal")] static extern void GooseHaptics_Impact(int style);
        [DllImport("__Internal")] static extern void GooseHaptics_Notification(int type);

        // Core Haptics layer
        [DllImport("__Internal")] static extern int GooseHaptics_CoreAvailable();
        [DllImport("__Internal")] static extern void GooseHaptics_Transient(float intensity, float sharpness);
        [DllImport("__Internal")] static extern void GooseHaptics_Continuous(float duration, float intensity, float sharpness);
        [DllImport("__Internal")] static extern void GooseHaptics_Pattern(int id, float intensity);
        [DllImport("__Internal")] static extern void GooseHaptics_SetPaused(int paused);
#endif

        void Awake()
        {
            hapticsEnabled = PlayerPrefs.GetInt(PrefsKey, 1) != 0;
            NativeAvailable = ProbeNative();
            CoreHapticsAvailable = ProbeCoreHaptics();
#if UNITY_EDITOR
            if (!s_EditorNoticeLogged)
            {
                s_EditorNoticeLogged = true;
                GooseLog.Info("Haptics simulated in Editor: requests are counted, nothing vibrates.");
            }
#endif
        }

        void OnApplicationPause(bool paused)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (!CoreHapticsAvailable) return;
            try
            {
                GooseHaptics_SetPaused(paused ? 1 : 0);
            }
            catch (Exception e)
            {
                DisableCoreHaptics(e);
            }
#endif
        }

        /// <summary>Turns haptics on or off and persists the choice in PlayerPrefs.</summary>
        public void SetEnabled(bool on)
        {
            hapticsEnabled = on;
            PlayerPrefs.SetInt(PrefsKey, on ? 1 : 0);
            PlayerPrefs.Save();
        }

        // ------------------------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------------------------

        public void Light() => Pulse(Strength.Light);
        public void Medium() => Pulse(Strength.Medium);
        public void Heavy() => Pulse(Strength.Heavy);

        /// <summary>
        /// Simple tap on the event channel. Core Haptics transient
        /// (Light 0.4/0.5, Medium 0.7/0.5, Heavy 1.0/0.6 intensity/sharpness), else a UIKit impact.
        /// </summary>
        public void Pulse(Strength strength)
        {
            if (!Accept(false)) return;
            if (CoreHapticsAvailable)
            {
                StrengthToCore(strength, out float intensity, out float sharpness);
                if (TryCoreTransient(intensity, sharpness)) return;
            }
            UiKitImpact((int)strength);
        }

        /// <summary>
        /// One transient tap with explicit intensity / sharpness (0..1). <paramref name="ambient"/>
        /// selects the rate-limit channel. UIKit fallback maps the intensity to Light / Medium / Heavy.
        /// </summary>
        public void Transient(float intensity, float sharpness, bool ambient = false)
        {
            if (!Accept(ambient)) return;
            if (CoreHapticsAvailable && TryCoreTransient(intensity, sharpness)) return;
            UiKitImpact((int)StrengthFromIntensity(intensity));
        }

        /// <summary>
        /// Continuous buzz of <paramref name="duration"/> seconds (native clamps to 0.02..2.0) that fades
        /// out over its last 30 %. Event channel. UIKit fallback is a single impact.
        /// </summary>
        public void Continuous(float duration, float intensity, float sharpness)
        {
            if (!Accept(false)) return;
            if (CoreHapticsAvailable && TryCoreContinuous(duration, intensity, sharpness)) return;
            UiKitImpact((int)StrengthFromIntensity(intensity));
        }

        /// <summary>
        /// Plays an authored pattern scaled by <paramref name="intensity"/> (0..1). Heartbeat, Footstep
        /// and WingBeat use the ambient channel, everything else the event channel. UIKit fallback is an
        /// impact of matching strength (Success uses the success notification).
        /// </summary>
        public void Play(Pattern pattern, float intensity = 1f)
        {
            if (!Accept(IsAmbient(pattern))) return;
            if (CoreHapticsAvailable && TryCorePattern(pattern, intensity)) return;
            if (pattern == Pattern.Success) UiKitNotification(0);
            else UiKitImpact((int)FallbackStrength(pattern));
        }

        /// <summary>
        /// Success / failure notification (used for game over). Not rate limited so it always lands.
        /// Success plays <see cref="Pattern.Success"/> with Core Haptics; failure is the UIKit error
        /// notification.
        /// </summary>
        public void Notify(bool success)
        {
            if (!hapticsEnabled) return;
            PulseCount++;
            if (success && CoreHapticsAvailable && TryCorePattern(Pattern.Success, 1f)) return;
            UiKitNotification(success ? 0 : 2);
        }

        // ------------------------------------------------------------------------------------
        // Rate limiting
        // ------------------------------------------------------------------------------------

        /// <summary>Applies the enabled flag and the per-channel rate limit; counts accepted requests.</summary>
        bool Accept(bool ambient)
        {
            if (!hapticsEnabled) return false;
            float now = Time.unscaledTime;
            if (ambient)
            {
                if (now < m_NextAmbientAllowed) return false;
                m_NextAmbientAllowed = now + ambientMinInterval;
            }
            else
            {
                if (now < m_NextEventAllowed) return false;
                m_NextEventAllowed = now + eventMinInterval;
            }
            PulseCount++;
            return true;
        }

        // ------------------------------------------------------------------------------------
        // Mappings
        // ------------------------------------------------------------------------------------

        static bool IsAmbient(Pattern pattern)
        {
            return pattern == Pattern.Heartbeat || pattern == Pattern.Footstep || pattern == Pattern.WingBeat;
        }

        static void StrengthToCore(Strength strength, out float intensity, out float sharpness)
        {
            switch (strength)
            {
                case Strength.Light:
                    intensity = 0.4f; sharpness = 0.5f;
                    break;
                case Strength.Heavy:
                    intensity = 1.0f; sharpness = 0.6f;
                    break;
                default:
                    intensity = 0.7f; sharpness = 0.5f;
                    break;
            }
        }

        static Strength StrengthFromIntensity(float intensity)
        {
            if (intensity < 0.55f) return Strength.Light;
            if (intensity < 0.85f) return Strength.Medium;
            return Strength.Heavy;
        }

        static Strength FallbackStrength(Pattern pattern)
        {
            switch (pattern)
            {
                case Pattern.Catch:
                case Pattern.LungeLaunch:
                    return Strength.Heavy;
                case Pattern.LungeWindup:
                case Pattern.Landing:
                case Pattern.EggCrack:
                case Pattern.RageRumble:
                    return Strength.Medium;
                default:
                    return Strength.Light;
            }
        }

        // ------------------------------------------------------------------------------------
        // Native: Core Haptics layer. Each returns false when it could not fire (then the caller
        // falls back to UIKit). A native exception disables Core Haptics for the session.
        // ------------------------------------------------------------------------------------

        bool TryCoreTransient(float intensity, float sharpness)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                GooseHaptics_Transient(intensity, sharpness);
                return true;
            }
            catch (Exception e)
            {
                DisableCoreHaptics(e);
            }
#endif
            return false;
        }

        bool TryCoreContinuous(float duration, float intensity, float sharpness)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                GooseHaptics_Continuous(duration, intensity, sharpness);
                return true;
            }
            catch (Exception e)
            {
                DisableCoreHaptics(e);
            }
#endif
            return false;
        }

        bool TryCorePattern(Pattern pattern, float intensity)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                GooseHaptics_Pattern((int)pattern, intensity);
                return true;
            }
            catch (Exception e)
            {
                DisableCoreHaptics(e);
            }
#endif
            return false;
        }

        void DisableCoreHaptics(Exception e)
        {
            if (!CoreHapticsAvailable) return;
            CoreHapticsAvailable = false;
            Debug.LogWarning("[GooseBrawl] Core Haptics failed, falling back to UIKit haptics: " + e.Message);
        }

        static bool ProbeNative()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                if (GooseHaptics_IsSupported() == 1)
                {
                    GooseHaptics_Prepare();
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GooseBrawl] Native haptics unavailable: " + e.Message);
            }
#endif
            return false;
        }

        static bool ProbeCoreHaptics()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                if (GooseHaptics_CoreAvailable() == 1)
                {
                    GooseLog.Info("Core Haptics available.");
                    return true;
                }
                GooseLog.Info("Core Haptics unavailable, using UIKit feedback generators.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GooseBrawl] Core Haptics probe failed, using UIKit feedback generators: " + e.Message);
            }
#endif
            return false;
        }

        // ------------------------------------------------------------------------------------
        // Native: UIKit layer (original behaviour) and platform fallbacks
        // ------------------------------------------------------------------------------------

        // style: 0 = light, 1 = medium, 2 = heavy
        void UiKitImpact(int style)
        {
            try
            {
                if (NativeAvailable)
                {
                    NativeImpact(style);
                    return;
                }
                FallbackVibrate();
            }
            catch (Exception e)
            {
                // UIKit is the last resort: if even that throws, stop trying for this session
                // (runtime only, the persisted preference is untouched).
                Debug.LogWarning("[GooseBrawl] Haptic pulse failed, haptics disabled for this session: " + e.Message);
                hapticsEnabled = false;
            }
        }

        // type: 0 = success, 1 = warning, 2 = error
        void UiKitNotification(int type)
        {
            try
            {
                if (NativeAvailable)
                {
                    NativeNotification(type);
                    return;
                }
                FallbackVibrate();
            }
            catch (Exception e)
            {
                if (m_NotifyWarned) return;
                m_NotifyWarned = true;
                Debug.LogWarning("[GooseBrawl] Notification haptic failed: " + e.Message);
            }
        }

        static void NativeImpact(int style)
        {
#if UNITY_IOS && !UNITY_EDITOR
            GooseHaptics_Impact(style);
#endif
        }

        static void NativeNotification(int type)
        {
#if UNITY_IOS && !UNITY_EDITOR
            GooseHaptics_Notification(type);
#endif
        }

        static void FallbackVibrate()
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
            Handheld.Vibrate();
#endif
        }
    }
}
