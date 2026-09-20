using System.IO;
using UnityEditor;
using UnityEngine;

namespace GooseBrawl.Editor
{
    /// <summary>Goose Brawl > Export Voice Lines: GooseLines.cs -> backend/goose-brain/scripts/lines.json (the Worker's bank + pregen input).</summary>
    public static class GooseVoiceExport
    {
        public const string LinesJsonPath = "backend/goose-brain/scripts/lines.json";

        [MenuItem("Goose Brawl/Export Voice Lines", false, 30)]
        public static void Export()
        {
            var dir = Path.GetDirectoryName(LinesJsonPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(LinesJsonPath, GooseLines.ToJson());
            int missing = 0;
            foreach (var folder in new[] { GooseLines.MaleVoiceFolder, GooseLines.FemaleVoiceFolder })
                foreach (var kv in GooseLines.Lines)
                    for (int i = 0; i < kv.Value.Length; i++)
                        if (Resources.Load<AudioClip>("GooseVoice/" + folder + "/" + GooseLines.Key(kv.Key) + "_" + i) == null) missing++;
            Debug.Log("[Goose Brawl] Voice lines exported to " + LinesJsonPath + (missing > 0 ? " (" + missing + " bank clips missing: run `npm run pregen` in backend/goose-brain)" : " (voice bank complete)"));
        }
    }
}
