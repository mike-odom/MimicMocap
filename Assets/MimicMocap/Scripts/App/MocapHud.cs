using MimicMocap.BlazePose;
using UnityEngine;

namespace MimicMocap
{
    /// <summary>Immediate-mode overlay: status, a camera thumbnail with landmarks, and the hotkey list.</summary>
    [RequireComponent(typeof(MocapDirector))]
    public sealed class MocapHud : MonoBehaviour
    {
        public int thumbnailWidth = 320;
        MocapDirector m_Director;
        GUIStyle m_Style;
        GUIStyle m_Small;
        Texture2D m_Dot;

        void Awake()
        {
            m_Director = GetComponent<MocapDirector>();
            m_Dot = new Texture2D(1, 1);
            m_Dot.SetPixel(0, 0, Color.white);
            m_Dot.Apply();
        }

        void OnGUI()
        {
            if (!m_Director.showHud) return;
            if (m_Style == null)
            {
                m_Style = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = new Color(1f, 0.85f, 0.4f) } };
                m_Small = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(0.8f, 0.8f, 0.8f) } };
            }

            var src = m_Director.ActiveSource;
            var inp = m_Director.ActiveInput;
            var pose = m_Director.SmoothedPose;

            var y = 10f;
            GUI.Label(new Rect(12, y, 800, 24), $"{(src != null ? src.Name : "no source")}  |  {(src != null ? src.Status : "")}", m_Style); y += 22;
            GUI.Label(new Rect(12, y, 800, 24), $"{(inp != null ? inp.Name : "no input")} {(inp != null && inp.IsReady ? inp.Width + "x" + inp.Height : "(starting)")}", m_Small); y += 18;
            GUI.Label(new Rect(12, y, 800, 24), $"render {1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):0} fps  |  pose {m_Director.InferenceFps:0.0} fps, {m_Director.LastInferenceMs:0.0} ms  |  score {pose.Score:0.00}  |  {(pose.Held ? "HOLDING" : pose.Tracked ? "TRACKING" : m_Director.IdleActive ? "attract loop" : "searching")}{(m_Director.ghostStage ? "  |  ghost stage" : "")}", m_Small); y += 18;
            GUI.Label(new Rect(12, y, 800, 24), $"mirror {(m_Director.mirror ? "on" : "off")}  |  view {m_Director.visual}{(m_Director.CharacterReady ? "" : " (no character rig)")}  |  smoothing {m_Director.smoothing.minCutoff:0.0} Hz  |  {(m_Director.UsingVmc ? "hands from VMC" : m_Director.Hands != null ? m_Director.Hands.Status : "hands off")}{(m_Director.stabilizeHands && m_Director.HandStabilizerP1 != null ? $"  |  steady L {m_Director.HandStabilizerP1.LeftTrackWeight:0.00} R {m_Director.HandStabilizerP1.RightTrackWeight:0.00}" : "  |  steady off")}{(m_Director.HandFallbackActive ? $"  |  fallback {m_Director.Hands.FallbackHits}" : "  |  fallback off")}", m_Small); y += 18;
            if (m_Director.performers > 1)
            {
                var p2 = m_Director.Performer2Active
                    ? $"performer 2: {(m_Director.SmoothedPose2.Tracked ? "TRACKING" : "searching")}  |  {(m_Director.CurrentCharacter2 < 0 ? "Bones" : m_Director.Slots[m_Director.CurrentCharacter2].DisplayName)}"
                    : "performer 2: needs the Maxine source (press S) ";
                GUI.Label(new Rect(12, y, 900, 24), p2, m_Small); y += 18;
            }
            if (!string.IsNullOrEmpty(m_Director.LastMessage)) { GUI.Label(new Rect(12, y, 900, 24), m_Director.LastMessage, m_Small); y += 18; }

            var help = "H hud   M mirror   1/2/3 skeleton/character/both   Tab character   U hand menu   T two performers   X ghost stage   I attract loop   S source (BlazePose/Maxine/VMC)   G hands   K hand fallback   Y steady hands   V BlazePose variant   C next webcam   O rotate camera   P webcam/video   N/B next/prev video   R reset   [ ] smoothing   -/= zoom   arrows move figure   0 reset framing   F fullscreen   Esc quit";
            GUI.Label(new Rect(12, Screen.height - 26, Screen.width - 24, 24), help, m_Small);

            DrawThumbnail(inp, src, pose);
        }

        void DrawThumbnail(IFrameSource inp, IPoseSource src, PoseFrame pose)
        {
            if (inp == null || !inp.IsReady || inp.Current == null) return;
            var aspect = inp.Width / (float)inp.Height;
            var w = thumbnailWidth;
            var h = Mathf.RoundToInt(w / aspect);
            if (h > thumbnailWidth) { h = thumbnailWidth; w = Mathf.RoundToInt(h * aspect); } // portrait camera: cap the height instead
            var rect = new Rect(Screen.width - w - 12, 12, w, h);
            // In mirror mode the overlays are in mirrored image space, so show the camera as a mirror too.
            if (m_Director.mirror)
                GUI.DrawTexture(new Rect(rect.x + rect.width, rect.y, -rect.width, rect.height), inp.Current, ScaleMode.StretchToFill, false);
            else
                GUI.DrawTexture(rect, inp.Current, ScaleMode.StretchToFill, false);

            var box = pose.ImageBox;
            if (m_Director.mirror) box = new Rect(1f - box.xMax, box.y, box.width, box.height);
            if (box.width > 0f)
            {
                var bx = rect.x + box.x * w;
                var by = rect.y + (1f - box.yMax) * h;
                GUI.color = pose.Tracked ? new Color(0.3f, 1f, 0.4f, 0.9f) : new Color(1f, 0.3f, 0.3f, 0.9f);
                DrawRect(new Rect(bx, by, box.width * w, box.height * h), 2f);
                GUI.color = Color.white;
            }

            if (src is BlazePoseSource blaze && pose.Tracked)
            {
                for (var i = 0; i < BlazePoseLayout.BodyLandmarks; i++)
                {
                    var p = blaze.LastImageLandmarks[i];
                    var vis = blaze.LastVisibility[i];
                    var px = m_Director.mirror ? 1f - p.x : p.x;
                    GUI.color = new Color(1f, 0.6f, 0.1f, Mathf.Clamp01(vis));
                    GUI.DrawTexture(new Rect(rect.x + px * w - 2, rect.y + (1f - p.y) * h - 2, 4, 4), m_Dot);
                }
                GUI.color = Color.white;
            }

            // Hand landmarks (already mirrored with the pose when mirror mode is on). VMC hands carry no image positions.
            if (m_Director.UsingVmc) return;
            foreach (var hand in new[] { pose.LeftHand, pose.RightHand })
            {
                if (!hand.Tracked || !hand.HasImageLandmarks) continue;
                GUI.color = new Color(0.3f, 1f, 0.9f, 0.9f);
                for (var i = 0; i < HandFrame.LandmarkCount; i++)
                {
                    var p = hand.ImageLandmarks[i];
                    GUI.DrawTexture(new Rect(rect.x + p.x * w - 1.5f, rect.y + (1f - p.y) * h - 1.5f, 3, 3), m_Dot);
                }
                GUI.color = Color.white;
            }
        }

        void DrawRect(Rect r, float thickness)
        {
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, thickness), m_Dot);
            GUI.DrawTexture(new Rect(r.x, r.yMax - thickness, r.width, thickness), m_Dot);
            GUI.DrawTexture(new Rect(r.x, r.y, thickness, r.height), m_Dot);
            GUI.DrawTexture(new Rect(r.xMax - thickness, r.y, thickness, r.height), m_Dot);
        }
    }
}
