using System;
using System.Collections;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// The goose's hops. Two flavours share one routine:
    ///  - the LUNGE: crouch, flap like mad, honk, a high parabolic hop at the player; catches if it lands within reach.
    ///  - the flap-DASH: a quick low hop covering 1.6-2.4 m that never lands closer than 1.8 m. It is the
    ///    "perceived speed" of the chase: the goose looks faster and more aggressive without its real speed climbing.
    /// </summary>
    public class GooseAttackController : MonoBehaviour
    {
        public struct HopSpec
        {
            public float crouchDuration, duration, height, stopDistance, homing, maxDistance;
            /// <summary>1 = symmetric arc; below 1 the peak comes late, right in front of the player's face.</summary>
            public float peakSkew;
            public bool canCatch, isDash;

            /// <summary>The flying lunge. Peaks near camera height so it crosses the phone's view.</summary>
            public static HopSpec Lunge(GooseAttackController a, float cameraHeight) => new HopSpec
            {
                crouchDuration = a.crouchDuration, duration = a.lungeDuration, height = Mathf.Clamp(Mathf.Max(a.lungeHeight, cameraHeight - 0.5f), 0.5f, 1.2f),
                stopDistance = a.landingStopDistance, homing = a.homing, maxDistance = 99f, peakSkew = 0.65f, canCatch = true, isDash = false
            };

            /// <summary>Point-blank catch: a quick rise into the player's face instead of a peck below the frame.</summary>
            public static HopSpec CatchLunge(GooseAttackController a, float cameraHeight) => new HopSpec
            {
                crouchDuration = 0.22f, duration = 0.55f, height = Mathf.Clamp(cameraHeight - 0.45f, 0.5f, 1.1f),
                stopDistance = a.landingStopDistance, homing = 0.6f, maxDistance = 99f, peakSkew = 0.6f, canCatch = true, isDash = false
            };

            public static HopSpec Dash(float length, float stopDistance) => new HopSpec
            {
                crouchDuration = 0.22f, duration = 0.6f, height = 0.28f, stopDistance = stopDistance,
                homing = 0.15f, maxDistance = length, peakSkew = 1f, canCatch = false, isDash = true
            };
        }

        [Header("Lunge")]
        [Tooltip("Peak height of the hop in meters (0.4 - 0.8).")]
        public float lungeHeight = 0.85f;
        public float lungeDuration = 0.8f;
        public float crouchDuration = 0.45f;
        public float lungeCooldown = 7f;
        [Tooltip("Player is caught if within this distance when the goose lands.")]
        public float catchRadius = 0.95f;
        [Tooltip("The lunge lands this far short of the player so the goose never overlaps the camera.")]
        public float landingStopDistance = 0.5f;
        [Tooltip("The goose will not lunge before the chase has lasted this long.")]
        public float minChaseTimeBeforeLunge = 8f;
        [Tooltip("How strongly the lunge keeps homing on a moving player (0 = straight line).")]
        public float homing = 0.35f;

        public bool IsLunging { get; private set; }
        public bool IsHopping { get; private set; }
        public int LungeCount { get; private set; }
        public float LastLungeTime { get; private set; } = -99f;

        float m_NextLungeTime;

        public bool CanLunge(float chaseTime) => !IsHopping && chaseTime >= minChaseTimeBeforeLunge && Time.time >= m_NextLungeTime;

        public void ResetCooldown(float delay)
        {
            m_NextLungeTime = Time.time + delay;
            IsLunging = false;
            IsHopping = false;
        }

        public IEnumerator LungeRoutine(GooseChaseController goose, Func<Vector3> targetProvider, Action<bool> onDone)
        {
            return HopRoutine(goose, HopSpec.Lunge(this, CameraHeight()), targetProvider, onDone);
        }

        /// <summary>Quick rising catch used when the goose is already within reach.</summary>
        public IEnumerator CatchLungeRoutine(GooseChaseController goose, Func<Vector3> targetProvider, Action<bool> onDone)
        {
            return HopRoutine(goose, HopSpec.CatchLunge(this, CameraHeight()), targetProvider, onDone);
        }

        float CameraHeight()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.Player == null) return 1.3f;
            return Mathf.Clamp(mgr.Player.Position.y - transform.position.y, 0.6f, 2f);
        }

        public IEnumerator HopRoutine(GooseChaseController goose, HopSpec spec, Func<Vector3> targetProvider, Action<bool> onDone)
        {
            IsHopping = true;
            IsLunging = !spec.isDash;
            if (!spec.isDash) { LungeCount++; LastLungeTime = Time.time; }
            var mgr = GooseGameManager.Instance;
            var mv = goose.Movement;
            var vis = goose.Visual;
            mv.ExternalControl = true;
            mv.Stop();

            // ---- crouch & flap ------------------------------------------------------------------
            vis.Play(GooseAnimationResolver.Slot.Flap, 0.08f, spec.isDash ? 1.7f : 1.5f, true);
            vis.Procedural.Flapping = true;
            vis.Procedural.FlapIntensity = 1f;
            if (spec.isDash)
            {
                mgr.Audio.PlayFlap(goose.bodySource);
                vis.FeatherBurst(6);
                if (UnityEngine.Random.value < 0.3f) goose.Honk(AudioManager.HonkKind.Angry);
            }
            else
            {
                goose.Honk(AudioManager.HonkKind.Angry);
                mgr.Audio.PlayFlap(goose.bodySource);
                vis.FeatherBurst();
                mgr.Haptics.Play(HapticsService.Pattern.LungeWindup);
            }

            float t = 0f;
            while (t < spec.crouchDuration)
            {
                float dt = Time.deltaTime;
                t += dt;
                float k = t / spec.crouchDuration;
                vis.Procedural.SquashStretch = Mathf.Lerp(1f, spec.isDash ? 0.85f : 0.72f, Mathf.Sin(k * Mathf.PI));
                Vector3 face = targetProvider() - transform.position;
                mv.FaceTowards(face, dt);
                yield return null;
            }

            // ---- launch --------------------------------------------------------------------------
            if (!spec.isDash) mgr.NotifyLungeStarted();
            vis.Play(spec.isDash ? GooseAnimationResolver.Slot.Fly : GooseAnimationResolver.Slot.Jump, 0.05f, spec.isDash ? 1.3f : 1.2f, true);
            if (spec.isDash)
            {
                mgr.Audio.PlayDash(goose.bodySource);
                mgr.Danger.Shake(0.15f);
                mgr.Haptics.Transient(0.35f + 0.08f * goose.Tier, 0.3f); // the push-off, felt more as the chase escalates
            }
            else
            {
                mgr.Audio.PlayLungeLaunch(goose.bodySource);
                goose.Honk(AudioManager.HonkKind.Rage);
                mgr.Haptics.Play(HapticsService.Pattern.LungeLaunch);
                mgr.Danger.Shake(0.7f);
                if (CinematicLookController.Instance != null) CinematicLookController.Instance.Impact(0.35f);
            }
            vis.Procedural.SquashStretch = 1.3f;
            mv.Airborne = true;

            Vector3 start = transform.position;
            Vector3 target = targetProvider();
            Vector3 dirFlat = target - start;
            dirFlat.y = 0f;
            if (dirFlat.sqrMagnitude < 1e-4f) dirFlat = transform.forward;
            float distToTarget = dirFlat.magnitude;
            dirFlat.Normalize();
            float hopLength = Mathf.Min(Mathf.Max(0f, distToTarget - spec.stopDistance), spec.maxDistance);
            Vector3 land = start + dirFlat * hopLength;
            land.y = start.y;

            t = 0f;
            while (t < spec.duration)
            {
                float dt = Time.deltaTime;
                t += dt;
                float k = Mathf.Clamp01(t / spec.duration);

                // Gentle homing toward the player's current position (never past the stop distance).
                Vector3 cur = targetProvider();
                Vector3 toCur = cur - start; toCur.y = 0f;
                float curDist = toCur.magnitude;
                if (curDist > 1e-3f)
                {
                    float len = Mathf.Min(Mathf.Max(0f, curDist - spec.stopDistance), spec.maxDistance);
                    Vector3 newLand = start + toCur / curDist * len;
                    newLand.y = start.y;
                    land = Vector3.Lerp(land, newLand, Mathf.Clamp01(spec.homing * dt * 6f));
                }

                Vector3 p = Vector3.Lerp(start, land, k);
                float kk = spec.peakSkew > 0f && spec.peakSkew != 1f ? Mathf.Pow(k, spec.peakSkew) : k;
                float h = spec.height * Mathf.Sin(kk * Mathf.PI);
                p = mv.KeepOutOfCamera(p);
                p.y = start.y + h;
                transform.position = p;
                vis.SetAirHeight(h);

                Vector3 look = land - start;
                look.y = 0f;
                if (look.sqrMagnitude > 1e-4f)
                {
                    float pitch = spec.isDash ? Mathf.Lerp(-12f, 10f, k) : Mathf.Lerp(-25f, 20f, k);
                    transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up) * Quaternion.Euler(pitch, 0f, 0f);
                }
                yield return null;
            }

            // ---- land ----------------------------------------------------------------------------
            Vector3 lp = transform.position;
            lp.y = mv.SampleFloor(lp);
            transform.position = lp;
            mv.Airborne = false; // the grounded pass pushes it out if the hop ended against a couch
            Vector3 flatForward = transform.forward;
            flatForward.y = 0f;
            mv.SetHeading(flatForward);
            vis.SetAirHeight(0f);
            vis.Procedural.Flapping = false;
            vis.Procedural.SquashStretch = spec.isDash ? 0.82f : 0.7f;
            vis.LandingEffect();
            mgr.Audio.PlayFootstep(goose.bodySource, spec.isDash ? 0.85f : 1f);
            float dist = mgr.Player.FlatDistanceTo(transform.position);
            if (spec.isDash)
            {
                if (dist < 4f) mgr.Haptics.Play(HapticsService.Pattern.Landing, Mathf.Clamp01(1f - dist / 4f) * 0.8f + 0.2f);
            }
            else
            {
                mgr.Haptics.Play(HapticsService.Pattern.Landing);
            }

            bool caught = spec.canCatch && dist <= catchRadius;

            if (!spec.isDash) m_NextLungeTime = Time.time + lungeCooldown;
            IsLunging = false;
            IsHopping = false;
            mv.ExternalControl = false;
            mv.ClearStuck();
            onDone?.Invoke(caught);
        }
    }
}
