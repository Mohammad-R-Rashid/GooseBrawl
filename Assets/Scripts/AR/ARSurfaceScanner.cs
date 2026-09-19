using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GooseBrawl
{
    /// <summary>Watches plane detection and reports when a usable patch of floor exists.</summary>
    public class ARSurfaceScanner : MonoBehaviour
    {
        [Tooltip("Square meters a horizontal plane needs before we accept it as floor.")]
        public float minFloorPlaneArea = 0.3f;
        public float pollInterval = 0.25f;

        public bool HasFloor { get; private set; }
        public int FloorPlaneCount { get; private set; }
        public int VerticalPlaneCount { get; private set; }
        public float LargestFloorArea { get; private set; }
        public float EstimatedFloorY { get; private set; }

        bool m_Scanning;
        float m_NextPoll;

        public void BeginScan()
        {
            m_Scanning = true;
            HasFloor = false;
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.AR.IsMock && mgr.Mock != null) mgr.Mock.BeginScan();
        }

        public void EndScan()
        {
            m_Scanning = false;
        }

        void Update()
        {
            if (!m_Scanning || Time.time < m_NextPoll) return;
            m_NextPoll = Time.time + pollInterval;

            var mgr = GooseGameManager.Instance;
            if (mgr == null) return;

            if (mgr.AR.IsMock)
            {
                HasFloor = mgr.Mock != null && mgr.Mock.FloorReady;
                EstimatedFloorY = mgr.Mock != null ? mgr.Mock.FloorY : 0f;
                FloorPlaneCount = HasFloor ? 1 : 0;
                return;
            }

            var pm = mgr.AR.planeManager;
            if (pm == null) return;

            int floors = 0, walls = 0;
            float largest = 0f, ySum = 0f;
            foreach (var plane in pm.trackables)
            {
                if (plane.trackingState != TrackingState.Tracking || plane.subsumedBy != null) continue;
                float area = plane.size.x * plane.size.y;
                if (plane.alignment == PlaneAlignment.HorizontalUp)
                {
                    if (area >= minFloorPlaneArea)
                    {
                        floors++;
                        ySum += plane.center.y;
                    }
                    if (area > largest) largest = area;
                }
                else if (plane.alignment == PlaneAlignment.Vertical)
                {
                    walls++;
                }
            }
            FloorPlaneCount = floors;
            VerticalPlaneCount = walls;
            LargestFloorArea = largest;
            if (floors > 0) EstimatedFloorY = ySum / floors;
            HasFloor = floors > 0;
        }
    }
}
