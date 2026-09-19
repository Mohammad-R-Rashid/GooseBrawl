using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Turns goose distance into feedback the player can feel and see: the post-processing danger grade,
    /// the HUD locator inputs and the impact effects. The felt danger (heartbeat haptic) lives in
    /// ChaosAudioController so it stays locked to the heartbeat sound.
    /// </summary>
    public class DangerFeedbackController : MonoBehaviour
    {
        public float Danger { get; private set; }
        public bool GooseVisible { get; private set; }
        public bool GooseBehind { get; private set; }
        public float GooseAngle { get; private set; }

        void Update()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.UI == null) return;
            var look = CinematicLookController.Instance;
            if (!mgr.ChaseActive || mgr.Goose == null)
            {
                Danger = 0f;
                GooseVisible = false;
                GooseBehind = false;
                mgr.UI.SetDanger(0f, false, 0f, false);
                if (look != null) look.SetDanger(0f);
                return;
            }

            var goose = mgr.Goose;
            Danger = goose.Danger01;
            Vector3 goosePos = goose.transform.position + Vector3.up * 0.3f;
            GooseVisible = mgr.Player.IsInView(goosePos, 0.05f);
            GooseAngle = mgr.Player.SignedAngleTo(goosePos);
            GooseBehind = Mathf.Abs(GooseAngle) > 95f;
            mgr.UI.SetDanger(Danger, GooseBehind, GooseAngle, GooseVisible);
            if (look != null) look.SetDanger(Danger);
        }

        public void Flash(Color color, float duration)
        {
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.UI != null) mgr.UI.Flash(color, duration);
        }

        public void Shake(float intensity)
        {
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.UI != null) mgr.UI.Shake(intensity);
        }

        /// <summary>The goose arrives: a small lens punch, no red paint.</summary>
        public void SpawnEffect()
        {
            var look = CinematicLookController.Instance;
            if (look != null) look.Impact(0.3f);
            Shake(0.25f);
        }

        /// <summary>Caught: hard impact grade + a short warm-red flash.</summary>
        public void CaughtEffect()
        {
            var mgr = GooseGameManager.Instance;
            var look = CinematicLookController.Instance;
            if (look != null) look.Impact(1f);
            if (mgr != null && mgr.UI != null)
            {
                mgr.UI.Flash(new Color(1f, 0.25f, 0.15f, 0.35f), 0.5f);
                mgr.UI.Shake(0.8f);
            }
        }

        public void ResetEffects()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.UI != null) mgr.UI.SetDanger(0f, false, 0f, false);
            var look = CinematicLookController.Instance;
            if (look != null)
            {
                look.SetDanger(0f);
                look.SetRage(false);
                look.SetSlowMotion(false);
            }
        }
    }
}
