using System.Diagnostics;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Logging that compiles out of release device builds. Info() only exists in the Editor and in
    /// Development builds; Warn()/Error() always exist. Keeps the device console quiet for the demo.
    /// </summary>
    public static class GooseLog
    {
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Info(string message)
        {
            UnityEngine.Debug.Log("[GooseBrawl] " + message);
        }

        public static void Warn(string message)
        {
            UnityEngine.Debug.LogWarning("[GooseBrawl] " + message);
        }

        public static void Error(string message)
        {
            UnityEngine.Debug.LogError("[GooseBrawl] " + message);
        }
    }
}
