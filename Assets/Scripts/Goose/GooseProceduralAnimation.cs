using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Procedural animation layered on top of (or instead of) the clips, applied in LateUpdate so it
    /// wins over the Animator: head tracking toward the phone, angry sideways glances, wing flaps,
    /// body tilt, squash/stretch, honk head-bobs, pecks and a fallback walk cycle if there are no clips.
    /// Works on world-space axes so it does not care about the model's bone conventions.
    /// </summary>
    public class GooseProceduralAnimation : MonoBehaviour
    {
        [Header("Head tracking")]
        public float maxHeadYaw = 55f;
        public float maxHeadPitch = 25f;
        public float headTurnSpeed = 540f;
        [Tooltip("Seconds between random look-around glances.")]
        public float glanceInterval = 3.5f;
        public float glanceDuration = 0.45f;
        public float glanceAngle = 35f;

        [Header("Wings")]
        public float flapFrequency = 8f;
        public float flapAngle = 50f;

        [Header("Body")]
        public float runTiltDeg = 9f;
        public float bobAmplitude = 0.025f;
        public float bobFrequency = 5.5f;
        public float legScrambleAngle = 35f;
        public float scaleRecoverSpeed = 6f;

        // Live inputs set by the chase / attack controllers
        public bool HasLookTarget;
        public Vector3 LookTarget;
        public bool Flapping;
        [Range(0f, 1f)] public float FlapIntensity = 1f;
        [Range(0f, 1f)] public float MoveSpeed01;
        public float BodyTilt;
        public float SquashStretch = 1f;
        public bool UseFallbackLocomotion;
        /// <summary>Procedural wing flapping is only used when the model has no wing animation of its own.</summary>
        public bool AllowProceduralWings;

        GooseVisualController m_Vis;
        Transform m_Root;
        float m_Yaw, m_Pitch;
        float m_NextGlance;
        float m_GlanceUntil;
        float m_GlanceYaw;
        float m_HonkBob;
        float m_Peck;
        float m_CurrentSquash = 1f;
        Vector3 m_ModelBaseScale = Vector3.one;
        Vector3 m_ModelBasePos;
        Quaternion m_ModelBaseRot = Quaternion.identity;
        float m_ThinkUntil;

        public void Bind(GooseVisualController vis)
        {
            m_Vis = vis;
            m_Root = vis.transform;
            if (vis.modelRoot != null)
            {
                m_ModelBaseScale = vis.modelRoot.localScale;
                m_ModelBasePos = vis.modelRoot.localPosition;
                m_ModelBaseRot = vis.modelRoot.localRotation;
            }
            m_NextGlance = Time.time + glanceInterval;
        }

        /// <summary>Quick head bob when the goose honks.</summary>
        public void TriggerHonkGesture()
        {
            m_HonkBob = 1f;
        }

        /// <summary>Head thrust forward (peck) — used when the goose catches the player without a lunge.</summary>
        public void PeckPulse()
        {
            m_Peck = 1f;
        }

        public void PauseToThink(float seconds)
        {
            m_ThinkUntil = Time.time + seconds;
        }

        void LateUpdate()
        {
            if (m_Vis == null || m_Root == null) return;
            float dt = Time.deltaTime;
            float t = Time.time;

            // ---- squash / stretch, tilt and bob on the model root -------------------------------
            m_CurrentSquash = Mathf.MoveTowards(m_CurrentSquash, SquashStretch, scaleRecoverSpeed * dt);
            SquashStretch = Mathf.MoveTowards(SquashStretch, 1f, scaleRecoverSpeed * 0.5f * dt);
            if (m_Vis.modelRoot != null)
            {
                float s = Mathf.Max(0.3f, m_CurrentSquash);
                float inv = 1f / Mathf.Sqrt(s);
                m_Vis.modelRoot.localScale = new Vector3(m_ModelBaseScale.x * inv, m_ModelBaseScale.y * s, m_ModelBaseScale.z * inv);

                float bob = 0f;
                if (UseFallbackLocomotion && MoveSpeed01 > 0.02f)
                    bob = Mathf.Abs(Mathf.Sin(t * bobFrequency * Mathf.PI * 2f * (0.6f + MoveSpeed01))) * bobAmplitude * (0.4f + MoveSpeed01);
                float side = Mathf.Sin(t * 7f) * 2f * MoveSpeed01;
                m_Vis.modelRoot.localPosition = m_ModelBasePos + new Vector3(0f, bob + m_Peck * 0.02f, m_Peck * 0.08f);
                m_Vis.modelRoot.localRotation = m_ModelBaseRot * Quaternion.Euler(BodyTilt, 0f, side);
            }

            // ---- head / neck ------------------------------------------------------------------
            float targetYaw = 0f, targetPitch = 0f;
            if (HasLookTarget)
            {
                Vector3 headPos = m_Vis.headBone != null ? m_Vis.headBone.position : m_Root.position + Vector3.up * 0.6f;
                Vector3 to = LookTarget - headPos;
                Vector3 flat = to;
                flat.y = 0f;
                if (flat.sqrMagnitude > 1e-4f)
                {
                    targetYaw = Vector3.SignedAngle(m_Root.forward, flat.normalized, Vector3.up);
                    targetPitch = Mathf.Atan2(to.y, flat.magnitude) * Mathf.Rad2Deg;
                }
            }
            // Random angry sideways glance.
            if (t >= m_NextGlance)
            {
                m_NextGlance = t + glanceInterval * Random.Range(0.6f, 1.5f);
                m_GlanceUntil = t + glanceDuration;
                m_GlanceYaw = Random.value > 0.5f ? glanceAngle : -glanceAngle;
            }
            if (t < m_GlanceUntil) targetYaw += m_GlanceYaw;
            if (t < m_ThinkUntil) targetYaw += Mathf.Sin(t * 3f) * 25f;

            targetYaw = Mathf.Clamp(targetYaw, -maxHeadYaw, maxHeadYaw);
            targetPitch = Mathf.Clamp(targetPitch, -maxHeadPitch, maxHeadPitch);
            // Comically fast head turns.
            m_Yaw = Mathf.MoveTowardsAngle(m_Yaw, targetYaw, headTurnSpeed * dt);
            m_Pitch = Mathf.MoveTowardsAngle(m_Pitch, targetPitch, headTurnSpeed * dt);

            m_HonkBob = Mathf.MoveTowards(m_HonkBob, 0f, dt * 4f);
            m_Peck = Mathf.MoveTowards(m_Peck, 0f, dt * 3f);
            float honkPitch = Mathf.Sin(m_HonkBob * Mathf.PI) * 25f;
            float peckPitch = Mathf.Sin(m_Peck * Mathf.PI) * -45f;
            float pitch = m_Pitch + honkPitch + peckPitch;

            ApplyHeadRotation(m_Vis.neckBone, m_Yaw * 0.3f, pitch * 0.3f);
            ApplyHeadRotation(m_Vis.neck2Bone, m_Yaw * 0.3f, pitch * 0.3f);
            ApplyHeadRotation(m_Vis.headBone, m_Yaw * 0.4f, pitch * 0.4f);

            // ---- wings ------------------------------------------------------------------------
            if (AllowProceduralWings && Flapping && FlapIntensity > 0.01f)
            {
                float angle = Mathf.Sin(t * flapFrequency * Mathf.PI * 2f) * flapAngle * FlapIntensity;
                Vector3 axis = m_Root.forward;
                if (m_Vis.leftWingBone != null) m_Vis.leftWingBone.rotation = Quaternion.AngleAxis(angle, axis) * m_Vis.leftWingBone.rotation;
                if (m_Vis.rightWingBone != null) m_Vis.rightWingBone.rotation = Quaternion.AngleAxis(-angle, axis) * m_Vis.rightWingBone.rotation;
            }

            // ---- fallback legs when there is no walk clip ----------------------------------------
            if (UseFallbackLocomotion && MoveSpeed01 > 0.02f)
            {
                float phase = t * bobFrequency * Mathf.PI * 2f * (0.6f + MoveSpeed01);
                float leg = Mathf.Sin(phase) * legScrambleAngle * (0.3f + MoveSpeed01);
                Vector3 axis = m_Root.right;
                if (m_Vis.leftLegBone != null) m_Vis.leftLegBone.rotation = Quaternion.AngleAxis(leg, axis) * m_Vis.leftLegBone.rotation;
                if (m_Vis.rightLegBone != null) m_Vis.rightLegBone.rotation = Quaternion.AngleAxis(-leg, axis) * m_Vis.rightLegBone.rotation;
            }
        }

        void ApplyHeadRotation(Transform bone, float yaw, float pitch)
        {
            if (bone == null) return;
            Quaternion yawQ = Quaternion.AngleAxis(yaw, Vector3.up);
            Vector3 right = yawQ * m_Root.right;
            Quaternion pitchQ = Quaternion.AngleAxis(-pitch, right);
            bone.rotation = yawQ * pitchQ * bone.rotation;
        }
    }
}
