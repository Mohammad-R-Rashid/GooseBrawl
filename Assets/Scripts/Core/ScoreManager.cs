using UnityEngine;

namespace GooseBrawl
{
    /// <summary>Survival time based score. The only persistent data in the game (PlayerPrefs).</summary>
    public class ScoreManager : MonoBehaviour
    {
        const string BestScoreKey = "GooseBrawl.BestScore";
        const string BestTimeKey = "GooseBrawl.BestTime";
        const string GamesPlayedKey = "GooseBrawl.GamesPlayed";

        [Tooltip("Points per second survived.")]
        public float scoreMultiplier = 1f;

        public bool Running { get; private set; }
        public float SurvivalTime { get; private set; }
        public int Score => Mathf.FloorToInt(SurvivalTime * scoreMultiplier);
        public int BestScore { get; private set; }
        public float BestTime { get; private set; }
        public int GamesPlayed { get; private set; }
        public bool LastRunWasBest { get; private set; }

        void Awake()
        {
            Load();
        }

        void Update()
        {
            if (Running) SurvivalTime += Time.deltaTime;
        }

        public void Load()
        {
            BestScore = PlayerPrefs.GetInt(BestScoreKey, 0);
            BestTime = PlayerPrefs.GetFloat(BestTimeKey, 0f);
            GamesPlayed = PlayerPrefs.GetInt(GamesPlayedKey, 0);
        }

        public void StartRun()
        {
            SurvivalTime = 0f;
            Running = true;
            m_Ended = false;
            LastRunWasBest = false;
        }

        public void Pause() { Running = false; }
        public void Resume() { if (SurvivalTime >= 0f && !m_Ended) Running = true; }

        bool m_Ended;

        public void EndRun()
        {
            if (!Running && m_Ended) return;
            Running = false;
            m_Ended = true;
            GamesPlayed++;
            if (Score > BestScore || (Score == BestScore && SurvivalTime > BestTime))
            {
                BestScore = Score;
                BestTime = SurvivalTime;
                LastRunWasBest = true;
            }
            Save();
        }

        public void Save()
        {
            PlayerPrefs.SetInt(BestScoreKey, BestScore);
            PlayerPrefs.SetFloat(BestTimeKey, BestTime);
            PlayerPrefs.SetInt(GamesPlayedKey, GamesPlayed);
            PlayerPrefs.Save();
        }

        public static string FormatTime(float t)
        {
            if (t < 0f) t = 0f;
            int minutes = Mathf.FloorToInt(t / 60f);
            int seconds = Mathf.FloorToInt(t % 60f);
            int tenths = Mathf.FloorToInt((t * 10f) % 10f);
            return minutes + ":" + seconds.ToString("00") + "." + tenths;
        }
    }
}
