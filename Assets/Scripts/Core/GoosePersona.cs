using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Who the goose is and what it holds against you. The brain (Cloudflare) is the source of truth; this mirror in
    /// PlayerPrefs keeps the name and the grudge when the phone is offline, so the goose still remembers you.
    /// </summary>
    public class GoosePersona : MonoBehaviour
    {
        const string NameKey = "GooseBrawl.Persona.Name";
        const string TitleKey = "GooseBrawl.Persona.Title";
        const string GrudgeKey = "GooseBrawl.Persona.Grudge";
        const string ShoutKey = "GooseBrawl.Persona.LastShout";
        const string LastTimeKey = "GooseBrawl.Persona.LastTime";
        const string WinsKey = "GooseBrawl.Persona.Wins";
        const string FemaleKey = "GooseBrawl.Persona.Female";

        public string Name { get; private set; } = "";
        public string Title { get; private set; } = "";
        /// <summary>Which stock voice this goose speaks with, fixed for its whole life.</summary>
        public bool Female { get; private set; }
        public string VoiceFolder => Female ? GooseLines.FemaleVoiceFolder : GooseLines.MaleVoiceFolder;
        /// <summary>Rounds the goose has won against this player, across launches.</summary>
        public int Grudge { get; private set; }
        public int Wins { get; private set; }
        public string LastShout { get; private set; } = "";
        public float LastTime { get; private set; }
        /// <summary>Where the current name came from: "brain" or "bank".</summary>
        public string Source { get; private set; } = "bank";
        public bool Returning => Grudge + Wins > 0;

        void Awake()
        {
            Name = PlayerPrefs.GetString(NameKey, "");
            Title = PlayerPrefs.GetString(TitleKey, "");
            Grudge = PlayerPrefs.GetInt(GrudgeKey, 0);
            Wins = PlayerPrefs.GetInt(WinsKey, 0);
            LastShout = PlayerPrefs.GetString(ShoutKey, "");
            LastTime = PlayerPrefs.GetFloat(LastTimeKey, 0f);
            Female = PlayerPrefs.GetInt(FemaleKey, 0) == 1;
        }

        /// <summary>Deterministic offline persona when the brain has not named the goose yet.</summary>
        public void EnsureFallback(int gamesPlayed)
        {
            if (!string.IsNullOrEmpty(Name)) return;
            var p = GooseLines.PickPersona(gamesPlayed);
            Name = p.name;
            Title = p.title;
            Female = p.female;
            Source = "bank";
            Save();
        }

        public void ApplyFromBrain(string name, string title, int grudge, bool female)
        {
            if (string.IsNullOrEmpty(name)) return;
            Name = name.ToUpperInvariant();
            Female = female;
            Title = string.IsNullOrEmpty(title) ? Title : title.ToUpperInvariant();
            Grudge = Mathf.Max(Grudge, grudge);
            Source = "brain";
            Save();
        }

        public void RecordLoss(float survival)
        {
            Grudge++;
            LastTime = survival;
            Save();
        }

        public void RecordWin(float survival)
        {
            Wins++;
            LastTime = survival;
            Save();
        }

        public void RecordShout(string transcript)
        {
            if (string.IsNullOrEmpty(transcript)) return;
            LastShout = transcript;
            Save();
        }

        /// <summary>The line under the results title on repeat rounds.</summary>
        public string GrudgeLine()
        {
            if (Grudge <= 0) return "";
            if (Grudge == 1) return "STILL MAD.";
            if (Grudge == 2) return "VERY MAD.";
            if (Grudge < 5) return "UNREASONABLY MAD.";
            return "HOLDING A GRUDGE SINCE ROUND ONE.";
        }

        void Save()
        {
            PlayerPrefs.SetString(NameKey, Name);
            PlayerPrefs.SetString(TitleKey, Title);
            PlayerPrefs.SetInt(GrudgeKey, Grudge);
            PlayerPrefs.SetInt(WinsKey, Wins);
            PlayerPrefs.SetString(ShoutKey, LastShout);
            PlayerPrefs.SetFloat(LastTimeKey, LastTime);
            PlayerPrefs.SetInt(FemaleKey, Female ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
