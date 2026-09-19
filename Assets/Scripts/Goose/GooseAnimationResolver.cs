using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Fuzzy-matches whatever animation clips the goose model shipped with onto the handful of
    /// slots the game needs. Never assumes exact clip names. Prefers in-place ("_IP") clips.
    /// </summary>
    public class GooseAnimationResolver : MonoBehaviour
    {
        public enum Slot { Idle, Walk, Run, Flap, Attack, Lunge, Jump, Hit, Death, Fly, Land, Count }

        [Tooltip("Every AnimationClip found on the goose model. Filled by Goose Brawl > Setup Project.")]
        public AnimationClip[] clips;

        AnimationClip[] m_Resolved;
        public bool Resolved { get; private set; }
        public bool HasClips => clips != null && clips.Length > 0;

        static readonly string[][] k_Preferences =
        {
            /* Idle   */ new[] { "idle_1", "idle1", "idle", "stand", "breath" },
            /* Walk   */ new[] { "walk_f_ip", "walk_ip", "walk_f", "walking", "walk" },
            /* Run    */ new[] { "run_f_ip", "run_ip", "run_f", "running", "run", "sprint" },
            /* Flap   */ new[] { "hissing", "hiss", "flap", "angry", "threat", "fly_start_ip", "fly_start", "wing" },
            /* Attack */ new[] { "attack", "peck", "bite", "hit_f", "hit" },
            /* Lunge  */ new[] { "run_attack_ip", "run_attack", "attack", "peck", "jump" },
            /* Jump   */ new[] { "run_fly_start_ip", "fly_start_ip", "run_fly_start", "fly_start", "jump", "fly_f_ip", "fly", "hop" },
            /* Hit    */ new[] { "hit_f", "hit_m", "hit_b", "hit", "stun", "stunned", "damage" },
            /* Death  */ new[] { "death", "die", "dead", "sit_start" },
            /* Fly    */ new[] { "fly_f_ip", "fly_ip", "fly_f", "flying", "fly", "glide" },
            /* Land   */ new[] { "fly_end_landing_ip", "fly_end_landing", "landing", "land", "fly_end" },
        };

        public AnimationClip Get(Slot slot)
        {
            if (!Resolved) Resolve();
            return m_Resolved != null ? m_Resolved[(int)slot] : null;
        }

        public void Resolve()
        {
            m_Resolved = new AnimationClip[(int)Slot.Count];
            Resolved = true;
            if (!HasClips)
            {
                GooseLog.Info("Goose has no animation clips; using procedural animation only.");
                return;
            }
            var sb = new StringBuilder("[GooseBrawl] Animation clips resolved: ");
            for (int i = 0; i < (int)Slot.Count; i++)
            {
                m_Resolved[i] = Find(k_Preferences[i]);
                sb.Append((Slot)i).Append('=').Append(m_Resolved[i] != null ? m_Resolved[i].name : "none").Append("  ");
            }
            GooseLog.Info(sb.ToString());
        }

        AnimationClip Find(string[] preferences)
        {
            // Exact match first.
            foreach (var pref in preferences)
                foreach (var clip in clips)
                {
                    if (clip == null) continue;
                    var n = Normalize(clip.name);
                    if (IsExcluded(n, pref)) continue;
                    if (n == pref) return clip;
                }
            // Then substring match.
            foreach (var pref in preferences)
                foreach (var clip in clips)
                {
                    if (clip == null) continue;
                    var n = Normalize(clip.name);
                    if (IsExcluded(n, pref)) continue;
                    if (n.Contains(pref)) return clip;
                }
            return null;
        }

        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var n = name.ToLowerInvariant().Trim();
            int bar = n.LastIndexOf('|');
            if (bar >= 0) n = n.Substring(bar + 1);
            return n.Replace(' ', '_').Replace('-', '_');
        }

        static bool IsExcluded(string normalized, string pref)
        {
            if (normalized.Contains("__preview__")) return true;
            if ((normalized.EndsWith("_rm") || normalized.Contains("_rm_")) && !pref.Contains("_rm")) return true;
            string[] avoid = { "sit", "swim", "sleep", "drink", "eat", "turn" };
            foreach (var a in avoid)
                if (normalized.Contains(a) && !pref.Contains(a)) return true;
            return false;
        }

        public List<string> DescribeAll()
        {
            var list = new List<string>();
            if (clips == null) return list;
            foreach (var c in clips) if (c != null) list.Add(c.name);
            return list;
        }
    }
}
