using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace GooseBrawl
{
    /// <summary>
    /// Plays the resolved clips through a Playables mixer (no AnimatorController asset needed) and
    /// exposes the bones the procedural layer needs. Falls back to pure procedural animation when
    /// the model has no clips at all. Also owns the goose's particle effects.
    /// </summary>
    public class GooseVisualController : MonoBehaviour
    {
        [Header("Model")]
        [Tooltip("The imported model instance (child of the goose root).")]
        public Transform modelRoot;
        public Animator animator;
        public GooseAnimationResolver resolver;

        [Header("Bones (auto-found by name when empty)")]
        public Transform headBone;
        public Transform neckBone;
        public Transform neck2Bone;
        public Transform leftWingBone;
        public Transform rightWingBone;
        public Transform leftLegBone;
        public Transform rightLegBone;

        [Header("Shadow / FX")]
        public Transform blobShadow;

        [Header("Tuning")]
        public float crossFade = 0.15f;
        [Tooltip("Meters per second the walk clip was authored for.")]
        public float walkClipSpeedRef = 0.8f;
        [Tooltip("Meters per second the run clip was authored for.")]
        public float runClipSpeedRef = 1.6f;

        public GooseProceduralAnimation Procedural { get; private set; }
        public bool HasAnimation => m_UsePlayables;
        public GooseAnimationResolver.Slot CurrentSlot { get; private set; } = GooseAnimationResolver.Slot.Idle;

        PlayableGraph m_Graph;
        AnimationMixerPlayable m_Mixer;
        AnimationLayerMixerPlayable m_LayerMixer;
        AnimationClipPlayable m_WingPlayable;
        bool m_HasWingLayer;
        float m_WingWeight, m_WingTarget;
        /// <summary>Current weight of the wing-flap layer (0 = wings follow the locomotion clip).</summary>
        public float WingLayerWeight => m_WingWeight;
        public bool HasWingLayer => m_HasWingLayer;
        AnimationClipPlayable[] m_ClipPlayables;
        bool[] m_HasClip;
        float[] m_Weights;
        float[] m_TargetWeights;
        int m_Current = -1;
        float m_FadeDuration = 0.15f;
        bool m_UsePlayables;
        bool m_Initialized;
        Vector3 m_ShadowBaseScale = Vector3.one;
        ParticleSystem m_Dust, m_FootDust;
        ParticleSystem m_Feathers, m_FeatherTrail;
        bool m_TrailOn;

        public void Initialize()
        {
            if (m_Initialized) return;
            m_Initialized = true;

            if (modelRoot == null && transform.childCount > 0) modelRoot = transform.GetChild(0);
            if (animator == null && modelRoot != null) animator = modelRoot.GetComponentInChildren<Animator>(true);
            if (resolver == null) resolver = GetComponent<GooseAnimationResolver>();
            if (resolver == null) resolver = gameObject.AddComponent<GooseAnimationResolver>();

            FindBones();
            resolver.Resolve();
            BuildGraph();

            Procedural = GetComponent<GooseProceduralAnimation>();
            if (Procedural == null) Procedural = gameObject.AddComponent<GooseProceduralAnimation>();
            Procedural.Bind(this);
            Procedural.UseFallbackLocomotion = !m_UsePlayables || resolver.Get(GooseAnimationResolver.Slot.Walk) == null;
            Procedural.AllowProceduralWings = !m_UsePlayables || resolver.Get(GooseAnimationResolver.Slot.Flap) == null;

            if (blobShadow != null) m_ShadowBaseScale = blobShadow.localScale;
            StartCoroutine(PrepareEffects());
        }

        public bool EffectsReady => m_Dust != null && m_FootDust != null && m_Crumbs != null && m_Feathers != null && m_FeatherTrail != null;

        IEnumerator PrepareEffects()
        {
            // Allocate during the entrance, spread across frames. Do not emit anything while warming.
            yield return null;
            var mats = MaterialLibrary.Instance;
            if (m_Dust == null) m_Dust = ProceduralAssets.CreateDustPuff(transform, mats != null ? mats.Dust : null, 12);
            yield return null;
            if (m_FootDust == null) m_FootDust = ProceduralAssets.CreateFootDust(transform, mats != null ? mats.Dust : null);
            yield return null;
            if (m_Crumbs == null) m_Crumbs = ProceduralAssets.CreateCrumbPuff(transform, mats != null ? mats.Crumbs : null);
            yield return null;
            if (m_Feathers == null) m_Feathers = ProceduralAssets.CreateFeatherBurst(transform);
            yield return null;
            if (m_FeatherTrail == null)
            {
                m_FeatherTrail = ProceduralAssets.CreateFeatherTrail(transform);
                m_FeatherTrail.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        /// <summary>Swap the goose material (e.g. the grey variant) on every renderer without instantiating copies.</summary>
        public void ApplyMaterial(Material material)
        {
            if (material == null || modelRoot == null) return;
            foreach (var r in modelRoot.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = material;
                r.sharedMaterials = mats;
            }
        }

        void FindBones()
        {
            if (modelRoot == null) return;
            var all = modelRoot.GetComponentsInChildren<Transform>(true);
            if (headBone == null) headBone = FindBone(all, "head", null, "headtop", "headend");
            if (neckBone == null) neckBone = FindBone(all, "neck_1", null) ?? FindBone(all, "neck1", null) ?? FindBone(all, "neck", null, "neck_2", "neck_3", "neck2", "neck3");
            if (neck2Bone == null) neck2Bone = FindBone(all, "neck_2", null) ?? FindBone(all, "neck2", null);
            if (leftWingBone == null) leftWingBone = FindSided(all, "wing", true);
            if (rightWingBone == null) rightWingBone = FindSided(all, "wing", false);
            if (leftLegBone == null) leftLegBone = FindSided(all, "leg", true);
            if (rightLegBone == null) rightLegBone = FindSided(all, "leg", false);
            GooseLog.Info("Goose bones: head=" + N(headBone) + " neck=" + N(neckBone) + " neck2=" + N(neck2Bone) +
                          " wingL=" + N(leftWingBone) + " wingR=" + N(rightWingBone) + " legL=" + N(leftLegBone) + " legR=" + N(rightLegBone));
        }

        static string N(Transform t) => t != null ? t.name : "none";

        static Transform FindBone(Transform[] all, string contains, string mustAlso, params string[] mustNot)
        {
            Transform best = null;
            foreach (var t in all)
            {
                var n = t.name.ToLowerInvariant();
                if (!n.Contains(contains)) continue;
                if (mustAlso != null && !n.Contains(mustAlso)) continue;
                bool bad = false;
                foreach (var m in mustNot) if (n.Contains(m)) { bad = true; break; }
                if (bad) continue;
                if (best == null || n.Length < best.name.Length) best = t; // shortest name = most root-like
            }
            return best;
        }

        static Transform FindSided(Transform[] all, string part, bool left)
        {
            // Prefer "<part>_1" root bones (e.g. Wing_1.L) over the tips.
            string[] sideTokens = left ? new[] { ".l", "_l", "left", "l_" } : new[] { ".r", "_r", "right", "r_" };
            Transform best = null;
            int bestScore = int.MinValue;
            foreach (var t in all)
            {
                var n = t.name.ToLowerInvariant();
                if (!n.Contains(part)) continue;
                bool sided = false;
                foreach (var s in sideTokens)
                {
                    if (n.EndsWith(s) || n.Contains(s + "_") || n.Contains(s + ".") || n.StartsWith(s)) { sided = true; break; }
                }
                if (!sided) continue;
                // Exclude the other side explicitly.
                if (left && (n.EndsWith(".r") || n.EndsWith("_r") || n.Contains("right"))) continue;
                if (!left && (n.EndsWith(".l") || n.EndsWith("_l") || n.Contains("left"))) continue;
                int score = 0;
                if (n.Contains(part + "_1") || n.Contains(part + "1")) score += 10;
                if (n.Contains("ik")) score -= 20;
                score -= n.Length;
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return best;
        }

        void BuildGraph()
        {
            m_UsePlayables = false;
            if (animator == null || resolver == null || !resolver.HasClips) return;

            int slots = (int)GooseAnimationResolver.Slot.Count;
            m_HasClip = new bool[slots];
            int available = 0;
            for (int i = 0; i < slots; i++)
            {
                m_HasClip[i] = resolver.Get((GooseAnimationResolver.Slot)i) != null;
                if (m_HasClip[i]) available++;
            }
            if (available == 0) return;

            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            m_Graph = PlayableGraph.Create("GooseAnimation");
            m_Graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var output = AnimationPlayableOutput.Create(m_Graph, "GooseAnimOutput", animator);
            m_Mixer = AnimationMixerPlayable.Create(m_Graph, slots);
            m_ClipPlayables = new AnimationClipPlayable[slots];
            m_Weights = new float[slots];
            m_TargetWeights = new float[slots];
            for (int i = 0; i < slots; i++)
            {
                if (!m_HasClip[i]) continue;
                var clip = resolver.Get((GooseAnimationResolver.Slot)i);
                var cp = AnimationClipPlayable.Create(m_Graph, clip);
                cp.SetApplyFootIK(false);
                m_ClipPlayables[i] = cp;
                m_Graph.Connect(cp, 0, m_Mixer, i);
                m_Mixer.SetInputWeight(i, 0f);
            }
            // Wing layer: the Fly clip's wing motion, masked to the wing bones, blended over walk/run so the goose can flap on the move.
            m_LayerMixer = AnimationLayerMixerPlayable.Create(m_Graph, 2);
            m_Graph.Connect(m_Mixer, 0, m_LayerMixer, 0);
            m_LayerMixer.SetInputWeight(0, 1f);
            var wingClip = resolver.Get(GooseAnimationResolver.Slot.Fly) ?? resolver.Get(GooseAnimationResolver.Slot.Flap);
            if (wingClip != null && (leftWingBone != null || rightWingBone != null))
            {
                m_WingPlayable = AnimationClipPlayable.Create(m_Graph, wingClip);
                m_WingPlayable.SetApplyFootIK(false);
                m_WingPlayable.SetSpeed(1.25);
                m_Graph.Connect(m_WingPlayable, 0, m_LayerMixer, 1);
                m_LayerMixer.SetInputWeight(1, 0f);
                var mask = BuildWingMask();
                if (mask != null)
                {
                    m_LayerMixer.SetLayerMaskFromAvatarMask(1, mask);
                    m_HasWingLayer = true;
                }
            }
            output.SetSourcePlayable(m_LayerMixer);
            m_Graph.Play();
            m_UsePlayables = true;
            Play(GooseAnimationResolver.Slot.Idle, 0f, 1f, true);
        }

        /// <summary>Avatar mask containing only the wing bone hierarchies (paths relative to the Animator).</summary>
        AvatarMask BuildWingMask()
        {
            if (animator == null) return null;
            var paths = new List<string>();
            foreach (var wing in new[] { leftWingBone, rightWingBone })
            {
                if (wing == null) continue;
                foreach (var t in wing.GetComponentsInChildren<Transform>(true))
                    paths.Add(RelativePath(t, animator.transform));
            }
            if (paths.Count == 0) return null;
            var mask = new AvatarMask();
            mask.transformCount = paths.Count;
            for (int i = 0; i < paths.Count; i++)
            {
                mask.SetTransformPath(i, paths[i]);
                mask.SetTransformActive(i, true);
            }
            return mask;
        }

        static string RelativePath(Transform t, Transform root)
        {
            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>0 = wings as in the locomotion clip, 1 = full flight flapping layered over it.</summary>
        public void SetWingLayer(float weight)
        {
            m_WingTarget = Mathf.Clamp01(weight);
        }

        int ResolveIndex(GooseAnimationResolver.Slot slot)
        {
            if (m_HasClip == null) return -1;
            var chain = Fallbacks(slot);
            foreach (var s in chain)
                if (m_HasClip[(int)s]) return (int)s;
            return -1;
        }

        static IEnumerable<GooseAnimationResolver.Slot> Fallbacks(GooseAnimationResolver.Slot slot)
        {
            var S = slot;
            switch (slot)
            {
                case GooseAnimationResolver.Slot.Walk: return new[] { S, GooseAnimationResolver.Slot.Run, GooseAnimationResolver.Slot.Idle };
                case GooseAnimationResolver.Slot.Run: return new[] { S, GooseAnimationResolver.Slot.Walk, GooseAnimationResolver.Slot.Idle };
                case GooseAnimationResolver.Slot.Flap: return new[] { S, GooseAnimationResolver.Slot.Attack, GooseAnimationResolver.Slot.Jump, GooseAnimationResolver.Slot.Idle };
                case GooseAnimationResolver.Slot.Attack: return new[] { S, GooseAnimationResolver.Slot.Lunge, GooseAnimationResolver.Slot.Flap, GooseAnimationResolver.Slot.Idle };
                case GooseAnimationResolver.Slot.Lunge: return new[] { S, GooseAnimationResolver.Slot.Attack, GooseAnimationResolver.Slot.Jump, GooseAnimationResolver.Slot.Run };
                case GooseAnimationResolver.Slot.Jump: return new[] { S, GooseAnimationResolver.Slot.Flap, GooseAnimationResolver.Slot.Lunge, GooseAnimationResolver.Slot.Run };
                case GooseAnimationResolver.Slot.Hit: return new[] { S, GooseAnimationResolver.Slot.Flap, GooseAnimationResolver.Slot.Idle };
                case GooseAnimationResolver.Slot.Death: return new[] { S, GooseAnimationResolver.Slot.Hit, GooseAnimationResolver.Slot.Idle };
                case GooseAnimationResolver.Slot.Fly: return new[] { S, GooseAnimationResolver.Slot.Jump, GooseAnimationResolver.Slot.Flap, GooseAnimationResolver.Slot.Run };
                case GooseAnimationResolver.Slot.Land: return new[] { S, GooseAnimationResolver.Slot.Flap, GooseAnimationResolver.Slot.Jump, GooseAnimationResolver.Slot.Idle };
                default: return new[] { S, GooseAnimationResolver.Slot.Walk };
            }
        }

        /// <summary>Cross-fade to a slot. Speed scales the clip playback.</summary>
        public void Play(GooseAnimationResolver.Slot slot, float fade = -1f, float speed = 1f, bool restart = false)
        {
            CurrentSlot = slot;
            if (!m_UsePlayables) return;
            int idx = ResolveIndex(slot);
            if (idx < 0) return;
            if (idx == m_Current && !restart)
            {
                m_ClipPlayables[idx].SetSpeed(speed);
                return;
            }
            m_Current = idx;
            if (restart) m_ClipPlayables[idx].SetTime(0.0);
            m_ClipPlayables[idx].SetSpeed(speed);
            m_FadeDuration = fade < 0f ? crossFade : Mathf.Max(0.001f, fade);
            for (int i = 0; i < m_TargetWeights.Length; i++) m_TargetWeights[i] = i == idx ? 1f : 0f;
        }

        public void SetClipSpeed(GooseAnimationResolver.Slot slot, float speed)
        {
            if (!m_UsePlayables) return;
            int idx = ResolveIndex(slot);
            if (idx >= 0) m_ClipPlayables[idx].SetSpeed(speed);
        }

        /// <summary>Scale the locomotion clip so the feet roughly match the code-driven speed (times an urgency multiplier).</summary>
        public void SetLocomotionSpeed(float metersPerSecond, bool running, float multiplier = 1f)
        {
            if (!m_UsePlayables || m_Current < 0) return;
            if (CurrentSlot != GooseAnimationResolver.Slot.Walk && CurrentSlot != GooseAnimationResolver.Slot.Run) return;
            float reference = running ? runClipSpeedRef : walkClipSpeedRef;
            float speed = Mathf.Clamp(metersPerSecond / Mathf.Max(0.1f, reference) * multiplier, 0.6f, 3f);
            m_ClipPlayables[m_Current].SetSpeed(speed);
        }

        public float ClipLength(GooseAnimationResolver.Slot slot)
        {
            var c = resolver != null ? resolver.Get(slot) : null;
            return c != null ? c.length : 0.6f;
        }

        void Update()
        {
            if (!m_UsePlayables) return;
            if (m_HasWingLayer)
            {
                float w = Mathf.MoveTowards(m_WingWeight, m_WingTarget, Time.deltaTime * (m_WingTarget > m_WingWeight ? 6f : 3f));
                if (w != m_WingWeight)
                {
                    m_WingWeight = w;
                    m_LayerMixer.SetInputWeight(1, w);
                }
            }
            float step = Time.deltaTime / m_FadeDuration;
            for (int i = 0; i < m_Weights.Length; i++)
            {
                if (!m_HasClip[i]) continue;
                float w = Mathf.MoveTowards(m_Weights[i], m_TargetWeights[i], step);
                if (w != m_Weights[i])
                {
                    m_Weights[i] = w;
                    m_Mixer.SetInputWeight(i, w);
                }
            }
        }

        /// <summary>Keep the contact shadow on the floor while the goose is airborne.</summary>
        public void SetAirHeight(float height)
        {
            if (blobShadow == null) return;
            blobShadow.localPosition = new Vector3(0f, -height + 0.012f, 0.02f);
            float s = Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(height / 0.8f));
            blobShadow.localScale = m_ShadowBaseScale * s;
        }

        public void SpawnEffect()
        {
            var mats = MaterialLibrary.Instance;
            if (m_Dust == null) m_Dust = ProceduralAssets.CreateDustPuff(transform, mats != null ? mats.Dust : null, 12);
            if (m_Dust != null)
            {
                m_Dust.transform.position = transform.position + Vector3.up * 0.05f;
                m_Dust.Play();
            }
        }

        public void LandingEffect()
        {
            SpawnEffect();
        }

        /// <summary>Tiny puff at the feet, once per footstep.</summary>
        public void FootDust()
        {
            var mats = MaterialLibrary.Instance;
            if (m_FootDust == null) m_FootDust = ProceduralAssets.CreateFootDust(transform, mats != null ? mats.Dust : null);
            if (m_FootDust != null)
            {
                m_FootDust.transform.position = transform.position + Vector3.up * 0.02f;
                m_FootDust.Play();
            }
        }

        ParticleSystem m_Crumbs;

        /// <summary>Crumbs off the beak while eating bread.</summary>
        public void CrumbPuff()
        {
            var mats = MaterialLibrary.Instance;
            if (m_Crumbs == null) m_Crumbs = ProceduralAssets.CreateCrumbPuff(transform, mats != null ? mats.Crumbs : null);
            if (m_Crumbs == null) return;
            Vector3 beak = headBone != null ? headBone.position + transform.forward * 0.12f : transform.position + Vector3.up * 0.3f + transform.forward * 0.3f;
            m_Crumbs.transform.position = beak;
            m_Crumbs.transform.rotation = Quaternion.LookRotation(transform.forward + Vector3.up * 0.4f);
            m_Crumbs.Play();
        }

        /// <summary>A puff of little white feathers from the wings.</summary>
        public void FeatherBurst(int count = -1)
        {
            if (m_Feathers == null) m_Feathers = ProceduralAssets.CreateFeatherBurst(transform);
            if (m_Feathers == null) return;
            m_Feathers.transform.position = transform.position + Vector3.up * 0.45f;
            if (count > 0) m_Feathers.Emit(count);
            else m_Feathers.Play();
        }

        /// <summary>Continuous feather shedding while running (late-chase rage).</summary>
        public void SetFeatherTrail(bool on)
        {
            if (on && m_FeatherTrail == null) m_FeatherTrail = ProceduralAssets.CreateFeatherTrail(transform);
            if (m_FeatherTrail == null || m_TrailOn == on) return;
            m_TrailOn = on;
            var em = m_FeatherTrail.emission;
            em.enabled = on;
            if (on && !m_FeatherTrail.isPlaying) m_FeatherTrail.Play();
        }

        void OnDestroy()
        {
            if (m_Graph.IsValid()) m_Graph.Destroy();
        }
    }
}
