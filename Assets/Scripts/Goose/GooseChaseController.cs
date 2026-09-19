using System.Collections;
using UnityEngine;

namespace GooseBrawl
{
    public enum GooseState { Idle, Walk, Run, AngryFlap, JumpAttack, Dash, Glare, Stunned, GameOver }

    /// <summary>
    /// The goose brain. Chases the player's horizontal position, escalates over time in four tiers
    /// (more hops, faster animation, more honks, feathers) while its real speed stays modest, honks
    /// more the closer it gets, avoids scanned obstacles, throws angry flaps, flap-dashes and flying
    /// lunges, and catches the player when it gets close enough.
    /// Entrance: flies in along the player's line of sight, lands, then GLARES without moving until
    /// the player has actually seen it (so nobody loses the goose at the start).
    /// </summary>
    [RequireComponent(typeof(GooseMovement))]
    [RequireComponent(typeof(GooseObstacleAvoidance))]
    [RequireComponent(typeof(GooseVisualController))]
    [RequireComponent(typeof(GooseAttackController))]
    public class GooseChaseController : MonoBehaviour
    {
        [Header("Speeds (m/s) - real speed stays gentle; urgency comes from hops and animation")]
        public float walkSpeed = 0.9f;
        public float runSpeed = 1.1f;
        public float maxChaseSpeed = 1.6f;
        [Tooltip("Seconds after the walk phase until the goose reaches max speed.")]
        public float timeToMaxSpeed = 40f;
        [Tooltip("The goose only walks for this long after spawning (gives the player a head start).")]
        public float walkPhaseDuration = 3f;
        public float farCatchUpDistance = 5f;
        public float farCatchUpBoost = 1.25f;
        [Tooltip("Speed cap when the goose is right behind the player (a beat to turn around).")]
        public float nearCapSpeed = 1.3f;

        [Header("Distances (m)")]
        public float catchDistance = 0.8f;
        public float lungeTriggerDistance = 1.7f;
        public float farDistance = 6f;

        [Header("Entrance (fly-in + glare)")]
        [Tooltip("How far in front of the player the goose appears before flying in.")]
        public float flyInDistance = 6.5f;
        public float flyHeight = 1.7f;
        public float flySpeed = 3.2f;
        [Tooltip("Seconds between squawks while flying in.")]
        public float flyHonkInterval = 0.8f;
        [Tooltip("After landing the goose glares and does not move until it has been seen this long (cumulative seconds in view).")]
        public float glareSeenDuration = 0.5f;
        [Tooltip("Maximum glare time before the chase starts regardless.")]
        public float glareMaxDuration = 4.5f;
        [Tooltip("After the glare the goose cannot catch or lunge for this long (time to turn around and run).")]
        public float gracePeriod = 2f;

        [Header("Escalation")]
        [Tooltip("Chase seconds at which tiers 1, 2 and 3 (rage) begin.")]
        public float[] tierStartTimes = { 8f, 18f, 30f };
        [Tooltip("Seconds between flap-dashes per tier (tier 0 never dashes).")]
        public float[] dashCooldownByTier = { 0f, 6.5f, 4.5f, 3f };
        public float dashMinDistance = 2.4f;
        public float dashMaxDistance = 6.5f;
        [Tooltip("A dash never lands closer to the player than this (dashes are theatre, lunges catch).")]
        public float dashStopDistance = 1.8f;
        public float rageSpeedMultiplier = 1.06f;
        public float rageHonkIntervalMultiplier = 0.55f;
        public float rageLungeCooldown = 4f;

        [Header("Brain")]
        [Tooltip("Steering decisions per second (4-8).")]
        public float replanRate = 6f;
        public float angryFlapInterval = 9f;
        public float angryFlapDuration = 1.1f;
        [Range(0f, 1f)] public float thinkPauseChance = 0.25f;
        public float thinkPauseDuration = 0.7f;
        public float stuckRecoverDuration = 0.8f;

        [Header("Wing flaps on the move (geese flap constantly while running)")]
        public float runFlapBurstMin = 0.6f;
        public float runFlapBurstMax = 1.2f;
        public float runFlapPauseMin = 1.6f;
        public float runFlapPauseMax = 3.4f;

        [Header("Honks")]
        public float honkIntervalFar = 5.5f;
        public float honkIntervalNear = 1.1f;
        [Range(0f, 0.9f)] public float honkJitter = 0.3f;

        [Header("Audio sources (3D)")]
        public AudioSource voiceSource;
        public AudioSource bodySource;
        public float footstepInterval = 0.32f;

        public GooseState State { get; private set; } = GooseState.Idle;
        public GooseMovement Movement { get; private set; }
        public GooseObstacleAvoidance Avoidance { get; private set; }
        public GooseVisualController Visual { get; private set; }
        public GooseAttackController Attack { get; private set; }

        public float DistanceToPlayer { get; private set; } = 99f;
        /// <summary>0 = far away, 1 = about to be caught.</summary>
        public float Danger01 { get; private set; }
        public float ChaseTime { get; private set; }
        /// <summary>Escalation tier 0-3 (3 = rage).</summary>
        public int Tier { get; private set; }
        public bool Paused { get; set; }
        public bool Rage => Tier >= 3;
        public bool FlyingIn { get; private set; }
        public bool Glaring { get; private set; }
        /// <summary>Cumulative seconds the goose has been in view since it landed.</summary>
        public float SeenTime { get; private set; }
        /// <summary>Honks you have survived this round (the HUD's secondary stat).</summary>
        public int HonkCount { get; private set; }
        public int DashCount { get; private set; }
        public float RageStartTime => tierStartTimes != null && tierStartTimes.Length >= 3 ? tierStartTimes[2] : 30f;
        float m_GraceUntil;

        static readonly string[] k_HonkTexts = { "HONK!", "HONK!!", "HOOONK", "honk.", "HONK?", "HONKHONK" };
        static readonly string[] k_AngryTexts = { "HONK!!!", "HOOOONK!", "HONK HONK!", "GIVE. IT. BACK." };

        GooseGameManager m_Mgr;
        float m_NextReplan, m_NextHonk, m_NextFlap, m_NextFootstep, m_StateTimer, m_NextDash, m_DashBoostUntil;
        float m_NextRunFlap, m_RunFlapUntil, m_NextRunFlapSound;
        float m_NextCatchLunge;
        public int RunFlapCount { get; private set; }
        Vector3 m_LastDecisionDir = Vector3.forward;
        Vector3 m_RecoveryDir = Vector3.back;
        bool m_Spawned;
        bool m_RageAnnounced;
        int m_StuckCount;
        AudioLowPassFilter m_VoiceLp, m_BodyLp;
        float m_BehindCutoff = 22000f;
        float m_FlyHeight = 1.7f;

        void Awake()
        {
            Movement = GetComponent<GooseMovement>();
            Avoidance = GetComponent<GooseObstacleAvoidance>();
            Visual = GetComponent<GooseVisualController>();
            Attack = GetComponent<GooseAttackController>();
        }

        public void Initialize(GooseGameManager mgr)
        {
            m_Mgr = mgr;
            var mask = mgr.Environment.EnvironmentMask;
            Movement.environmentMask = mask;
            Avoidance.environmentMask = mask;
            EnsureAudioSources();
            Visual.Initialize();
            if (mgr.Materials != null) Visual.ApplyMaterial(mgr.Materials.PickGooseMaterial());
        }

        void EnsureAudioSources()
        {
            var audio = m_Mgr != null ? m_Mgr.Audio : null;
            if (voiceSource == null)
            {
                var go = new GameObject("Voice");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 0.5f, 0.2f);
                voiceSource = go.AddComponent<AudioSource>();
            }
            if (bodySource == null)
            {
                var go = new GameObject("Body");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 0.25f, 0f);
                bodySource = go.AddComponent<AudioSource>();
            }
            if (audio != null)
            {
                audio.ConfigureGooseSource(voiceSource);
                audio.ConfigureGooseSource(bodySource);
            }
            m_VoiceLp = voiceSource.GetComponent<AudioLowPassFilter>();
            if (m_VoiceLp == null) m_VoiceLp = voiceSource.gameObject.AddComponent<AudioLowPassFilter>();
            m_BodyLp = bodySource.GetComponent<AudioLowPassFilter>();
            if (m_BodyLp == null) m_BodyLp = bodySource.gameObject.AddComponent<AudioLowPassFilter>();
            m_VoiceLp.cutoffFrequency = m_BodyLp.cutoffFrequency = 22000f;
        }

        public void Spawn(Vector3 position, Vector3 facing, float floorY)
        {
            Movement.FloorY = floorY;
            Avoidance.FloorY = floorY;
            Movement.Teleport(position, facing);
            m_Spawned = true;
            ChaseTime = 0f;
            Tier = 0;
            m_StuckCount = 0;
            m_NextHonk = Time.time + 1.6f;
            m_NextFlap = Time.time + angryFlapInterval;
            m_NextReplan = 0f;
            m_NextDash = Time.time + 2f;
            Attack.ResetCooldown(Attack.minChaseTimeBeforeLunge);

            // Spawn flourish: dust, feathers, flap, loud honk.
            Visual.SpawnEffect();
            Visual.FeatherBurst();
            m_Mgr.Audio.PlayFlap(bodySource);
            EnterAngryFlap(0.9f);
        }

        /// <summary>
        /// Cinematic entrance: appear far away along the player's line of sight, fly in low while squawking,
        /// land, glare until seen, then hand over to the chase (onLanded).
        /// </summary>
        public void FlyIn(Vector3 start, Vector3 landing, float floorY, System.Action onLanded)
        {
            FlyIn(start, landing, floorY, flyHeight, onLanded);
        }

        public void FlyIn(Vector3 start, Vector3 landing, float floorY, float height, System.Action onLanded)
        {
            m_FlyHeight = Mathf.Max(0.3f, height);
            Movement.FloorY = floorY;
            Avoidance.FloorY = floorY;
            Vector3 dir = landing - start;
            dir.y = 0f;
            Movement.Teleport(new Vector3(start.x, floorY + m_FlyHeight, start.z), dir);
            Movement.ExternalControl = true;
            m_Spawned = true;
            FlyingIn = true;
            Glaring = false;
            SeenTime = 0f;
            ChaseTime = 0f;
            Tier = 0;
            m_StuckCount = 0;
            StartCoroutine(FlyInRoutine(landing, floorY, onLanded));
        }

        IEnumerator FlyInRoutine(Vector3 landing, float floorY, System.Action onLanded)
        {
            Visual.Play(GooseAnimationResolver.Slot.Fly, 0.05f, 1.1f, true);
            Visual.Procedural.Flapping = true;
            Visual.Procedural.FlapIntensity = 1f;
            m_Mgr.Audio.StartWingBeat(bodySource);
            Vector3 start = transform.position;
            Vector3 flat = landing - start;
            flat.y = 0f;
            float total = Mathf.Max(0.5f, flat.magnitude);
            Vector3 dir = flat / total;
            float nextHonk = Time.time + 0.15f;
            float nextWingHaptic = Time.time + 0.2f;
            float travelled = 0f;
            float descentStart = Mathf.Max(0f, total - Mathf.Min(2.4f, total * 0.7f));

            while (travelled < total)
            {
                float dt = Time.deltaTime;
                travelled += flySpeed * dt;
                float k = Mathf.Clamp01(travelled / total);
                Vector3 p = start + dir * Mathf.Min(travelled, total);
                float height;
                if (travelled < descentStart) height = m_FlyHeight + 0.08f * Mathf.Sin(Time.time * 9f);
                else
                {
                    float d = Mathf.Clamp01((travelled - descentStart) / Mathf.Max(0.1f, total - descentStart));
                    height = Mathf.Lerp(m_FlyHeight, 0f, d * d);
                }
                p.y = floorY + height;
                transform.position = p;
                float pitch = travelled < descentStart ? -8f : Mathf.Lerp(-8f, 14f, (travelled - descentStart) / 2.4f);
                transform.rotation = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(pitch, 0f, 4f * Mathf.Sin(Time.time * 6f));
                Visual.SetAirHeight(height);
                Visual.Procedural.MoveSpeed01 = 1f;

                if (Time.time >= nextHonk)
                {
                    nextHonk = Time.time + flyHonkInterval * Random.Range(0.8f, 1.2f);
                    Honk(Random.value < 0.4f ? AudioManager.HonkKind.Rage : AudioManager.HonkKind.Angry);
                    if (Random.value < 0.5f) m_Mgr.Audio.PlayWhoosh(bodySource);
                }
                if (Time.time >= nextWingHaptic)
                {
                    nextWingHaptic = Time.time + 0.34f;
                    float near = Mathf.Clamp01(1f - DistanceToPlayerNow() / 7f);
                    m_Mgr.Haptics.Play(HapticsService.Pattern.WingBeat, 0.25f + 0.6f * near);
                }
                if (k >= 1f) break;
                yield return null;
            }

            // Land.
            m_Mgr.Audio.StopWingBeat(bodySource);
            Vector3 lp = transform.position;
            lp.y = floorY;
            transform.position = lp;
            Visual.SetAirHeight(0f);
            Movement.SetHeading(dir);
            Visual.Play(GooseAnimationResolver.Slot.Land, 0.05f, 1.1f, true);
            Visual.LandingEffect();
            Visual.FeatherBurst();
            m_Mgr.Audio.PlayFootstep(bodySource, 1f);
            m_Mgr.Haptics.Play(HapticsService.Pattern.Landing);
            yield return new WaitForSeconds(0.45f);

            // Glare: face the phone, honk, and stay put until the player has actually looked at the goose.
            FlyingIn = false;
            Glaring = true;
            SetState(GooseState.Glare);
            Visual.Play(GooseAnimationResolver.Slot.Idle, 0.25f, 1f);
            Visual.Procedural.Flapping = false;
            float glareT = 0f;
            float nextGlareHonk = 0.15f;
            int glareHonks = 0;
            while (glareT < glareMaxDuration && SeenTime < glareSeenDuration)
            {
                float dt = Time.deltaTime;
                glareT += dt;
                Vector3 toPlayer = m_Mgr.Player.FlatPosition - transform.position;
                Movement.FaceTowards(toPlayer, dt);
                if (m_Mgr.Player.IsInView(transform.position + Vector3.up * 0.45f, 0.02f)) SeenTime += dt;
                if (glareT >= nextGlareHonk && glareHonks < 3)
                {
                    glareHonks++;
                    nextGlareHonk = glareT + 1.1f;
                    Honk(glareHonks == 1 ? AudioManager.HonkKind.Dramatic : AudioManager.HonkKind.Angry);
                    if (glareHonks == 2) { Visual.Procedural.Flapping = true; Visual.Procedural.FlapIntensity = 0.6f; Visual.Play(GooseAnimationResolver.Slot.Flap, 0.1f, 1f, true); }
                }
                yield return null;
            }
            // Once seen, a short beat so the player registers it before it moves.
            float settle = 0f;
            while (settle < 0.35f)
            {
                settle += Time.deltaTime;
                Movement.FaceTowards(m_Mgr.Player.FlatPosition - transform.position, Time.deltaTime);
                yield return null;
            }

            Glaring = false;
            Movement.ExternalControl = false;
            Movement.ClearStuck();
            m_NextHonk = Time.time + 1.2f;
            m_NextFlap = Time.time + angryFlapInterval;
            m_NextReplan = 0f;
            m_NextDash = Time.time + 3f;
            m_GraceUntil = Time.time + gracePeriod;
            Attack.ResetCooldown(Attack.minChaseTimeBeforeLunge);
            EnterAngryFlap(0.7f);
            onLanded?.Invoke();
        }

        float DistanceToPlayerNow() => m_Mgr != null ? m_Mgr.Player.FlatDistanceTo(transform.position) : 99f;

        void Update()
        {
            if (!m_Spawned || m_Mgr == null) return;
            float dt = Time.deltaTime;
            var player = m_Mgr.Player;

            DistanceToPlayer = player.FlatDistanceTo(transform.position);
            Danger01 = 1f - Mathf.Clamp01((DistanceToPlayer - catchDistance) / Mathf.Max(0.1f, farDistance - catchDistance));
            Avoidance.HasEnvironmentData = m_Mgr.Environment.HasEnvironmentColliders;
            UpdateBehindYouFilter(dt);

            // Always glare at the phone.
            Visual.Procedural.HasLookTarget = true;
            Visual.Procedural.LookTarget = player.Position;

            if (State == GooseState.GameOver || State == GooseState.JumpAttack || State == GooseState.Dash || FlyingIn || Glaring) return;

            bool paused = Paused || !player.TrackingGood || !m_Mgr.ChaseActive;
            if (paused)
            {
                Movement.Stop();
                if (State != GooseState.Idle) SetState(GooseState.Idle);
                Visual.SetFeatherTrail(false);
                return;
            }

            ChaseTime += dt;
            UpdateTier();

            bool grace = Time.time < m_GraceUntil;
            if (!grace && DistanceToPlayer <= catchDistance && State != GooseState.Stunned)
            {
                // Never a peck below the frame: the catch is a rising flight into the player's face (it can still miss).
                if (Time.time >= m_NextCatchLunge) StartCatchLunge();
                else if (DistanceToPlayer <= catchDistance * 0.5f) Catch();
                return;
            }

            if (!grace && State != GooseState.Stunned && DistanceToPlayer <= lungeTriggerDistance && Attack.CanLunge(ChaseTime))
            {
                StartLunge();
                return;
            }

            if (State == GooseState.AngryFlap)
            {
                m_StateTimer -= dt;
                Movement.Stop();
                if (m_StateTimer <= 0f)
                {
                    Visual.Procedural.Flapping = false;
                    SetState(GooseState.Walk);
                }
                UpdateHonks();
                return;
            }

            if (State == GooseState.Stunned)
            {
                m_StateTimer -= dt;
                Movement.SetDesired(m_RecoveryDir, walkSpeed * 0.7f);
                if (m_StateTimer <= 0f)
                {
                    Movement.ClearStuck();
                    Visual.Procedural.Flapping = false;
                    SetState(GooseState.Walk);
                }
                return;
            }

            if (Time.time >= m_NextFlap && DistanceToPlayer > lungeTriggerDistance + 0.5f)
            {
                m_NextFlap = Time.time + angryFlapInterval * Random.Range(0.7f, 1.4f) * (Rage ? 0.55f : 1f);
                if (Random.value < thinkPauseChance && Tier < 2)
                {
                    Visual.Procedural.PauseToThink(thinkPauseDuration);
                    EnterAngryFlap(thinkPauseDuration, quiet: true);
                }
                else
                {
                    EnterAngryFlap(angryFlapDuration);
                }
                return;
            }

            if (Movement.Stuck)
            {
                EnterStunned();
                return;
            }

            if (!grace && TryDash()) return;

            if (Time.time >= m_NextReplan)
            {
                m_NextReplan = Time.time + 1f / Mathf.Max(1f, replanRate);
                Vector3 toPlayer = player.FlatPosition - transform.position;
                var decision = Avoidance.Choose(transform.position, toPlayer, Movement.Heading, DistanceToPlayer, m_StuckCount > 0);
                m_LastDecisionDir = decision.direction;
                if (decision.noEscape)
                {
                    EnterStunned();
                    return;
                }
                Movement.SetDesired(decision.direction, CurrentChaseSpeed() * decision.speedScale);
            }

            float spd = Movement.CurrentSpeed;
            var want = spd < 0.05f ? GooseState.Idle : (spd < (walkSpeed + runSpeed) * 0.5f ? GooseState.Walk : GooseState.Run);
            if (want != State) SetState(want);

            float urgency = 1f + 0.18f * Tier;
            Visual.SetLocomotionSpeed(spd, State == GooseState.Run, urgency);
            Visual.Procedural.MoveSpeed01 = Mathf.Clamp01(spd / maxChaseSpeed);
            float tilt = State == GooseState.Run ? Mathf.Lerp(9f, 15f, Tier / 3f) : 3f;
            Visual.Procedural.BodyTilt = tilt * Mathf.Clamp01(spd / maxChaseSpeed) + (Rage ? 3f : 0f);
            Visual.SetFeatherTrail(Tier >= 2 && spd > 0.5f);
            UpdateRunFlaps(spd);

            if (spd > 0.1f && Time.time >= m_NextFootstep)
            {
                float stepScale = Mathf.Clamp(walkSpeed / Mathf.Max(spd, 0.3f), 0.45f, 1.5f) / Mathf.Sqrt(urgency);
                m_NextFootstep = Time.time + footstepInterval * stepScale;
                float intensity = Mathf.Clamp01(spd / runSpeed);
                m_Mgr.Audio.PlayFootstep(bodySource, intensity);
                Visual.FootDust();
                if (DistanceToPlayer < 3f)
                    m_Mgr.Haptics.Play(HapticsService.Pattern.Footstep, Mathf.Clamp01(1f - DistanceToPlayer / 3f) * (0.35f + 0.65f * intensity));
            }

            UpdateHonks();
        }

        /// <summary>Bursts of wing flapping layered over the walk/run clips; more often and stronger as the tiers rise.</summary>
        void UpdateRunFlaps(float spd)
        {
            if (spd < 0.35f)
            {
                Visual.SetWingLayer(0f);
                return;
            }
            if (Time.time >= m_NextRunFlap)
            {
                float burst = Random.Range(runFlapBurstMin, runFlapBurstMax) * Mathf.Lerp(1f, 1.3f, Tier / 3f);
                m_RunFlapUntil = Time.time + burst;
                m_NextRunFlap = m_RunFlapUntil + Random.Range(runFlapPauseMin, runFlapPauseMax) * Mathf.Lerp(1f, 0.5f, Tier / 3f);
                m_NextRunFlapSound = Time.time + 0.32f;
                RunFlapCount++;
                m_Mgr.Audio.PlayFlap(bodySource);
                if (Tier >= 2) Visual.FeatherBurst(4);
            }
            bool flapping = Time.time < m_RunFlapUntil;
            if (flapping && Time.time >= m_NextRunFlapSound)
            {
                m_NextRunFlapSound = Time.time + 0.34f;
                m_Mgr.Audio.PlayFlap(bodySource);
            }
            float weight = flapping ? Mathf.Lerp(0.6f, 0.95f, Tier / 3f) : 0f;
            // Sprinting late in the chase: wings stay a little out between bursts.
            if (!flapping && State == GooseState.Run && Tier >= 2 && spd > 0.9f) weight = 0.22f;
            Visual.SetWingLayer(weight);
        }

        void UpdateTier()
        {
            int tier = 0;
            if (tierStartTimes != null)
                for (int i = 0; i < tierStartTimes.Length && i < 3; i++)
                    if (ChaseTime >= tierStartTimes[i]) tier = i + 1;
            if (tier == Tier) return;
            Tier = tier;
            if (Tier >= 3 && !m_RageAnnounced) EnterRage();
        }

        /// <summary>Muffle the goose when it is behind the player: the strongest "it's behind you" cue a phone speaker can give.</summary>
        void UpdateBehindYouFilter(float dt)
        {
            if (m_VoiceLp == null || m_BodyLp == null) return;
            float angle = Mathf.Abs(m_Mgr.Player.SignedAngleTo(transform.position));
            float behind = Mathf.Clamp01((angle - 60f) / 120f);
            float target = Mathf.Lerp(22000f, 2200f, behind * behind);
            // Distance dulls the goose too, so "closer" always sounds brighter and more present.
            float far = Mathf.Clamp01((DistanceToPlayer - 1.5f) / 7f);
            target = Mathf.Min(target, Mathf.Lerp(22000f, 4200f, far));
            float slowMo = m_Mgr.Audio != null ? m_Mgr.Audio.SlowMotionCutoff : 22000f;
            m_BehindCutoff = Mathf.Lerp(m_BehindCutoff, Mathf.Min(target, slowMo), 1f - Mathf.Exp(-dt * 6f));
            m_VoiceLp.cutoffFrequency = m_BehindCutoff;
            m_BodyLp.cutoffFrequency = m_BehindCutoff;
        }

        float CurrentChaseSpeed()
        {
            float speed;
            if (ChaseTime < walkPhaseDuration)
            {
                speed = walkSpeed;
            }
            else
            {
                float ramp = Mathf.Clamp01((ChaseTime - walkPhaseDuration) / Mathf.Max(1f, timeToMaxSpeed));
                speed = Mathf.Lerp(runSpeed, maxChaseSpeed, ramp);
            }
            if (DistanceToPlayer > farCatchUpDistance) speed *= farCatchUpBoost;
            if (Rage) speed *= rageSpeedMultiplier;
            if (Time.time < m_DashBoostUntil) speed *= 1.3f;
            // Give the player a beat to turn around when the goose is right behind them.
            if (DistanceToPlayer < catchDistance + 0.5f) speed = Mathf.Min(speed, nearCapSpeed);
            return speed;
        }

        /// <summary>Flap-dash: a quick low hop toward the player that stops well short. Cadence rises with the tier.</summary>
        bool TryDash()
        {
            if (Tier < 1 || Time.time < m_NextDash) return false;
            if (Attack.IsHopping || Time.time - Attack.LastLungeTime < 1f) return false;
            float minD = Rage ? 1.9f : dashMinDistance;
            if (DistanceToPlayer < minD || DistanceToPlayer > dashMaxDistance) return false;
            Vector3 toPlayer = m_Mgr.Player.FlatPosition - transform.position;
            toPlayer.y = 0f;
            float d = toPlayer.magnitude;
            if (d < 0.05f) return false;
            Vector3 dir = toPlayer / d;
            float hopLen = Mathf.Min(d - dashStopDistance, Random.Range(1.6f, 2.4f));
            if (hopLen < 0.6f) return false;
            float probe = hopLen + 0.3f;
            if (Avoidance.Clearance(transform.position, dir, probe) < probe - 0.01f || !Avoidance.FloorOk(transform.position + dir * hopLen))
            {
                m_NextDash = Time.time + 1f;
                return false;
            }
            int tier = Mathf.Clamp(Tier, 0, dashCooldownByTier.Length - 1);
            m_NextDash = Time.time + dashCooldownByTier[tier] * Random.Range(0.8f, 1.2f);
            DashCount++;
            SetState(GooseState.Dash);
            StartCoroutine(Attack.HopRoutine(this, GooseAttackController.HopSpec.Dash(hopLen, dashStopDistance), () => m_Mgr.Player.FlatPosition, _ =>
            {
                if (State != GooseState.Dash) return;
                SetState(GooseState.Run);
                m_DashBoostUntil = Time.time + 0.4f;
                m_NextReplan = 0f;
            }));
            return true;
        }

        void UpdateHonks()
        {
            if (Time.time < m_NextHonk) return;
            float interval = Mathf.Lerp(honkIntervalFar, honkIntervalNear, Danger01) * Random.Range(1f - honkJitter, 1f + honkJitter);
            interval *= Mathf.Lerp(1f, 0.7f, Tier / 3f);
            if (Rage) interval *= rageHonkIntervalMultiplier;
            m_NextHonk = Time.time + interval;
            var kind = Danger01 > 0.7f ? AudioManager.HonkKind.Near : (Danger01 > 0.35f ? AudioManager.HonkKind.Mid : AudioManager.HonkKind.Far);
            if (Rage && Random.value < 0.4f) kind = AudioManager.HonkKind.Rage;
            Honk(kind);
        }

        /// <summary>Play a honk: 3D audio, head gesture, a haptic when close and a comic bubble on the HUD, all on the same frame.</summary>
        public void Honk(AudioManager.HonkKind kind)
        {
            HonkCount++;
            m_Mgr.Audio.PlayGooseHonk(kind, voiceSource, Danger01);
            Visual.Procedural.TriggerHonkGesture();
            bool angry = kind == AudioManager.HonkKind.Angry || kind == AudioManager.HonkKind.Rage || kind == AudioManager.HonkKind.Dramatic;
            var texts = angry ? k_AngryTexts : k_HonkTexts;
            string text = texts[Random.Range(0, texts.Length)];
            float scale = angry ? 1.15f : Mathf.Lerp(0.8f, 1.05f, Danger01);
            float length = m_Mgr.Audio.HonkLength(kind);
            m_Mgr.UI.ShowHonk(transform.position, text, length + 0.25f, scale);
            m_Mgr.UI.PulseLocator();
            if (Danger01 > 0.5f || angry)
                m_Mgr.Haptics.Continuous(Mathf.Clamp(length, 0.12f, 0.6f), Mathf.Lerp(0.25f, 0.8f, Danger01), 0.6f);
        }

        void EnterRage()
        {
            m_RageAnnounced = true;
            Attack.lungeCooldown = rageLungeCooldown;
            Visual.FeatherBurst();
            Honk(AudioManager.HonkKind.Rage);
            m_Mgr.UI.ShowMessage("RAGE MODE", 1.4f, UITheme.Danger);
            m_Mgr.Haptics.Play(HapticsService.Pattern.RageRumble);
            if (CinematicLookController.Instance != null)
            {
                CinematicLookController.Instance.Impact(0.5f);
                CinematicLookController.Instance.SetRage(true);
            }
            m_NextFlap = Time.time + angryFlapInterval * 0.5f;
        }

        void EnterAngryFlap(float duration, bool quiet = false)
        {
            SetState(GooseState.AngryFlap);
            m_StateTimer = duration;
            Movement.Stop();
            Visual.Play(GooseAnimationResolver.Slot.Flap, 0.1f, 1.2f, true);
            Visual.Procedural.Flapping = !quiet;
            Visual.Procedural.FlapIntensity = 1f;
            if (!quiet)
            {
                m_Mgr.Audio.PlayFlap(bodySource);
                Visual.FeatherBurst();
                Honk(Rage ? AudioManager.HonkKind.Rage : AudioManager.HonkKind.Angry);
            }
        }

        void EnterStunned()
        {
            m_StuckCount++;
            SetState(GooseState.Stunned);
            m_StateTimer = stuckRecoverDuration + 0.3f * Mathf.Min(m_StuckCount, 3);
            Movement.ClearStuck();
            // Back up and turn away from the blocked direction, alternating sides.
            Vector3 away = -m_LastDecisionDir;
            float turn = (m_StuckCount % 2 == 0 ? 1f : -1f) * 60f;
            m_RecoveryDir = Quaternion.AngleAxis(turn, Vector3.up) * away;
            Visual.Play(GooseAnimationResolver.Slot.Hit, 0.1f, 1f, true);
            Visual.Procedural.Flapping = true;
            Visual.Procedural.FlapIntensity = 0.7f;
            Honk(AudioManager.HonkKind.Angry);
        }

        void StartCatchLunge()
        {
            SetState(GooseState.JumpAttack);
            Visual.SetFeatherTrail(false);
            StartCoroutine(Attack.CatchLungeRoutine(this, () => m_Mgr.Player.FlatPosition, caught =>
            {
                if (State == GooseState.GameOver) return;
                if (caught)
                {
                    Catch();
                }
                else
                {
                    m_NextCatchLunge = Time.time + 1.2f;
                    SetState(GooseState.Walk);
                    m_Mgr.NotifyLungeEnded(false);
                }
            }));
        }

        void StartLunge()
        {
            SetState(GooseState.JumpAttack);
            Visual.SetFeatherTrail(false);
            StartCoroutine(Attack.LungeRoutine(this, () => m_Mgr.Player.FlatPosition, caught =>
            {
                if (State == GooseState.GameOver) return;
                if (caught)
                {
                    Catch();
                }
                else
                {
                    SetState(GooseState.Walk);
                    m_Mgr.NotifyLungeEnded(false);
                }
            }));
        }

        void Catch()
        {
            if (State == GooseState.GameOver) return;
            SetState(GooseState.GameOver);
            Movement.Stop();
            Movement.ExternalControl = true;
            Visual.SetFeatherTrail(false);
            Vector3 face = m_Mgr.Player.FlatPosition - transform.position;
            Movement.SetHeading(face);
            Visual.Play(GooseAnimationResolver.Slot.Attack, 0.05f, 1.2f, true);
            Visual.Procedural.PeckPulse();
            Visual.Procedural.Flapping = true;
            Visual.Procedural.FlapIntensity = 1f;
            Visual.Procedural.SquashStretch = 1.25f;
            Visual.FeatherBurst(20);
            Danger01 = 1f;
            Honk(AudioManager.HonkKind.Rage);
            m_Mgr.OnPlayerCaught();
            StartCoroutine(VictoryRoutine());
        }

        /// <summary>Called by the manager on game over (idempotent).</summary>
        public void OnCaughtPlayer()
        {
            if (State != GooseState.GameOver) Catch();
        }

        IEnumerator VictoryRoutine()
        {
            yield return new WaitForSeconds(0.9f);
            Visual.Play(GooseAnimationResolver.Slot.Flap, 0.15f, 1f, true);
            for (int i = 0; i < 3; i++)
            {
                Visual.Procedural.Flapping = true;
                Visual.Procedural.PeckPulse();
                Honk(AudioManager.HonkKind.Near);
                yield return new WaitForSeconds(1.2f);
            }
            Visual.Procedural.Flapping = false;
            Visual.Play(GooseAnimationResolver.Slot.Idle, 0.3f, 1f);
        }

        void SetState(GooseState s)
        {
            if (State == s) return;
            State = s;
            if (s != GooseState.Walk && s != GooseState.Run) Visual.SetWingLayer(0f);
            switch (s)
            {
                case GooseState.Idle:
                    Visual.Procedural.Flapping = false;
                    Visual.Play(GooseAnimationResolver.Slot.Idle, 0.2f, 1f);
                    break;
                case GooseState.Walk:
                    Visual.Procedural.Flapping = false;
                    Visual.Play(GooseAnimationResolver.Slot.Walk, 0.15f, 1f);
                    break;
                case GooseState.Run:
                    Visual.Procedural.Flapping = false;
                    Visual.Play(GooseAnimationResolver.Slot.Run, 0.15f, 1f);
                    break;
            }
        }
    }
}
