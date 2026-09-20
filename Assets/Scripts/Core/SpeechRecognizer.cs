using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>Apple on-device speech recognition of the yell clip (Assets/Plugins/iOS/GooseSpeech.mm). Nothing leaves the phone.</summary>
    public static class GooseSpeech
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int GooseSpeech_Available();
        [DllImport("__Internal")] static extern int GooseSpeech_AuthorizationStatus();
        [DllImport("__Internal")] static extern void GooseSpeech_RequestAuthorization();
        [DllImport("__Internal")] static extern int GooseSpeech_Recognize(string wavPath, string contextCsv, float deadlineSeconds);
        [DllImport("__Internal")] static extern int GooseSpeech_Poll(int id, byte[] utf8Out, int cap);
#endif
        static readonly byte[] s_Buffer = new byte[512];
        static int s_Available = -1;

        /// <summary>An on-device recogniser exists for the phone's language (or English).</summary>
        public static bool Available
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                if (s_Available < 0) { try { s_Available = GooseSpeech_Available(); } catch { s_Available = 0; } }
                return s_Available == 1;
#else
                return false;
#endif
            }
        }

        /// <summary>0 not determined, 1 denied, 2 restricted, 3 authorized.</summary>
        public static int AuthorizationStatus
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                try { return GooseSpeech_AuthorizationStatus(); } catch { return 1; }
#else
                return 0;
#endif
            }
        }

        public static void RequestAuthorization()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try { GooseSpeech_RequestAuthorization(); } catch (Exception e) { GooseTelemetry.CaptureException(e, "speech.auth"); }
#endif
        }

        /// <summary>Start recognising a WAV file. Returns a request id, or -1 when unsupported.</summary>
        public static int Recognize(string wavPath, string contextCsv, float deadlineSeconds)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try { return GooseSpeech_Recognize(wavPath, contextCsv ?? "", deadlineSeconds); }
            catch (Exception e) { GooseTelemetry.CaptureException(e, "speech.start"); return -1; }
#else
            return -1;
#endif
        }

        /// <summary>0 pending, 1 done (transcript filled), -1 failed, -2 stale id.</summary>
        public static int Poll(int id, out string transcript)
        {
            transcript = "";
#if UNITY_IOS && !UNITY_EDITOR
            int status;
            try { status = GooseSpeech_Poll(id, s_Buffer, s_Buffer.Length); } catch { return -1; }
            if (status == 1)
            {
                int n = Array.IndexOf(s_Buffer, (byte)0);
                if (n < 0) n = s_Buffer.Length;
                transcript = Encoding.UTF8.GetString(s_Buffer, 0, n);
            }
            return status;
#else
            return -1;
#endif
        }
    }

    public enum ShoutIntent { None, Name, Apology }

    /// <summary>What the player shouted, as far as the goose cares: its name, an apology, or just noise.</summary>
    public static class ShoutClassifier
    {
        static readonly Dictionary<string, string[]> k_Aliases = new Dictionary<string, string[]>
        {
            { "KEVIN", new[] { "kevin", "kev", "kevan", "calvin" } },
            { "BRENDA", new[] { "brenda", "brendan", "brent" } },
            { "GARY", new[] { "gary", "garry", "jerry", "cary", "carrie" } },
            { "MARGARET", new[] { "margaret", "marge", "maggie", "margret" } },
            { "DR. HONK", new[] { "honk", "doctor", "doc" } },
            { "AGNES", new[] { "agnes", "agnus", "magnus" } },
            { "LORD FEATHERINGTON", new[] { "lord", "featherington", "feathers", "feather", "featherton" } },
            { "PAMELA", new[] { "pamela", "pam", "pamella" } },
            { "STEVE", new[] { "steve", "steven", "stephen" } },
            { "CHAD", new[] { "chad", "chadwick" } },
            { "DUKE", new[] { "duke", "dook" } },
            { "BARRY", new[] { "barry", "berry", "harry" } },
        };
        static readonly string[] k_Apology = { "sorry", "please", "stop", "good goose", "nice goose", "good boy", "good girl", "forgive", "apolog", "friend" };

        /// <summary>Lower-case words that count as the goose's name (aliases plus the name's own tokens, for brain-invented names).</summary>
        public static List<string> NameWords(string gooseName)
        {
            var words = new List<string>();
            string upper = (gooseName ?? "").Trim().ToUpperInvariant();
            if (k_Aliases.TryGetValue(upper, out var aliases)) words.AddRange(aliases);
            foreach (var tok in upper.Split(new[] { ' ', '.', '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = tok.ToLowerInvariant();
                if (t.Length >= 3 && !words.Contains(t)) words.Add(t);
            }
            return words;
        }

        /// <summary>Contextual strings that bias the recogniser toward the words we care about.</summary>
        public static string ContextCsv(string gooseName)
        {
            var sb = new StringBuilder();
            foreach (var w in NameWords(gooseName)) sb.Append(w).Append(',');
            foreach (var w in k_Apology) sb.Append(w).Append(',');
            return sb.ToString().TrimEnd(',');
        }

        public static ShoutIntent Classify(string transcript, string gooseName)
        {
            if (string.IsNullOrEmpty(transcript)) return ShoutIntent.None;
            string lower = transcript.ToLowerInvariant();
            var clean = new StringBuilder(lower.Length);
            foreach (char c in lower) clean.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '\'' ? c : ' ');
            string text = clean.ToString();
            var tokens = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var nameWord in NameWords(gooseName))
            {
                foreach (var tok in tokens)
                {
                    if (tok == nameWord) return ShoutIntent.Name;
                    if (nameWord.Length >= 5 && tok.Length >= 4 && tok.StartsWith(nameWord.Substring(0, 4))) return ShoutIntent.Name;
                }
            }
            foreach (var phrase in k_Apology) if (text.Contains(phrase)) return ShoutIntent.Apology;
            return ShoutIntent.None;
        }
    }
}
