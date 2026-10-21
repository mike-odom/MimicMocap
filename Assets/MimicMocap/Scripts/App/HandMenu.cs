using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace MimicMocap
{
    /// <summary>
    /// A row of glowing portals floating above the avatar's head. Hold a hand inside one for a moment to become
    /// what it shows: the bare bones, or any rigged character under CharacterRoot. Everything lives in world space,
    /// so the avatar's own hand (procedural skeleton or character rig) is the pointer and it behaves the same
    /// whether or not the view is mirrored.
    /// </summary>
    [RequireComponent(typeof(MocapDirector))]
    [DefaultExecutionOrder(20)]
    public sealed class HandMenu : MonoBehaviour
    {
        [Header("Behaviour")]
        [Tooltip("Hidden by default so the stage shows only the figure; U or -menu shows the portals, Tab still cycles characters.")]
        public bool visible;
        [Tooltip("Seconds a hand must stay inside a portal to press it.")]
        public float dwellSeconds = 0.9f;
        [Tooltip("Seconds after a press before another portal can be pressed.")]
        public float cooldownSeconds = 1.2f;

        [Header("Layout (metres, world space)")]
        [Tooltip("Height of the row centre. Hands reach it with a raised arm; a normalised avatar is about 1.75 m tall.")]
        public float rowHeight = 1.85f;
        [Tooltip("Z of the row. Slightly behind the avatar so a centred head passes in front of the middle portal.")]
        public float rowDepth = -0.3f;
        public float radius = 0.135f;
        public float spacing = 0.09f;
        public float hoverMargin = 0.03f;
        [Tooltip("How far in front of or behind the row a hand may be and still count.")]
        public float depthTolerance = 0.7f;
        [Tooltip("Log how each preview was framed (bounds, camera distance).")]
        public bool logPreviewFraming;

        [Header("Look")]
        public Color panelColor = new Color(0.035f, 0.02f, 0.05f);
        public Color frameColor = new Color(0.85f, 0.80f, 0.66f);
        public Color hoverColor = new Color(1.6f, 0.35f, 0.05f);
        public Color selectedColor = new Color(0.35f, 1.3f, 0.55f);
        [Tooltip("Ring colour for the second performer's current choice.")]
        public Color selected2Color = new Color(0.9f, 0.4f, 1.4f);
        [Range(0f, 2f)] public float frameGlow = 0.45f;
        public int previewSize = 256;

        /// <summary>Tests: pretend a hand is at this world position instead of reading the avatar.</summary>
        public Vector3? TestHandOverride;
        /// <summary>Tests: which performer (1 or 2) the override hand belongs to.</summary>
        public int TestHandPerformer = 1;
        /// <summary>Performer whose hand is in the hovered portal (1 or 2), or 0.</summary>
        public int HoverPerformer => m_Hovered >= 0 ? m_HoverPerformer : 0;

        /// <summary>Raised with the entry index when a portal is pressed (0 = bones, i = character i-1).</summary>
        public event Action<int> EntryPressed;

        public int EntryCount => m_Entries.Count;
        public int HoveredEntry => m_Hovered;
        public float DwellFraction => m_Hovered >= 0 ? Mathf.Clamp01(m_Dwell / Mathf.Max(0.01f, dwellSeconds)) : 0f;
        public int SelectedEntry => m_Director == null ? -1
            : m_Director.visual == MocapDirector.VisualMode.Skeleton || !m_Director.CharacterReady ? 0
            : m_Director.CurrentCharacter + 1;
        /// <summary>Entry the second performer shows, or -1 when there is no second performer.</summary>
        public int SelectedEntry2 => m_Director == null || !m_Director.Performer2Active ? -1 : m_Director.CurrentCharacter2 + 1;
        public string EntryLabel(int i) => m_Entries[i].Label;
        public Vector3 EntryWorldPosition(int i) => m_Entries[i].Root.position;
        public Texture EntryPreview(int i) => m_Entries[i].Preview;

        sealed class Entry
        {
            public string Label;
            public int CharacterIndex; // -1 = bare bones
            public Transform Root;
            public MeshRenderer Panel, Frame, Progress, Icon;
            public Mesh ProgressMesh;
            public Material PanelMat, FrameMat, ProgressMat, IconMat;
            public TextMesh Text;
            public RenderTexture Preview;
            public float Flash;
            public float Hover;
        }

        struct HandPoint
        {
            public Vector3 Position;
            public int Performer;
        }

        MocapDirector m_Director;
        Transform m_Root;
        readonly List<Entry> m_Entries = new List<Entry>();
        readonly List<HandPoint> m_Hands = new List<HandPoint>(8);
        int m_HoverPerformer;
        readonly PoseFrame m_Standing = new PoseFrame();
        Mesh m_DiscMesh, m_RingMesh, m_IconMesh, m_PlateMesh;
        Font m_Font;
        int m_Hovered = -1;
        float m_Dwell;
        float m_CooldownUntil;
        bool m_Armed = true;
        bool m_Dirty;

        void Awake()
        {
            m_Director = GetComponent<MocapDirector>();
        }

        void OnEnable()
        {
            m_Director.CharactersChanged += OnCharactersChanged;
        }

        void OnDisable()
        {
            m_Director.CharactersChanged -= OnCharactersChanged;
        }

        void Start()
        {
            Rebuild();
        }

        void OnDestroy()
        {
            Clear();
        }

        void OnCharactersChanged()
        {
            if (m_Entries.Count != m_Director.Slots.Length + 1) m_Dirty = true;
        }

        /// <summary>Recreate the portals from the director's current character slots.</summary>
        public void Rebuild()
        {
            Clear();
            m_Dirty = false;
            m_Root = new GameObject("HandMenu").transform;
            m_DiscMesh = BuildArc(null, 0f, radius, 1f, 48);
            m_RingMesh = BuildArc(null, radius * 0.94f, radius * 1.03f, 1f, 72);
            m_IconMesh = BuildIconDisc(radius * 0.965f, 48);
            m_PlateMesh = BuildPlate(radius * 2.3f, 0.075f);
            if (m_Font == null) m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var labels = new List<(string label, int index)> { ("Bones", -1) };
            for (var i = 0; i < m_Director.Slots.Length; i++)
                if (m_Director.Slots[i] != null) labels.Add((m_Director.Slots[i].DisplayName, i));

            // The scene camera looks down -Z, so +X is on the audience's left: lay the row out from +X so Bones comes first.
            var pitch = radius * 2f + spacing;
            var x0 = (labels.Count - 1) * pitch * 0.5f;
            for (var i = 0; i < labels.Count; i++)
                m_Entries.Add(CreateEntry(labels[i].label, labels[i].index, new Vector3(x0 - i * pitch, rowHeight, rowDepth)));

            RenderPreviews();
            RefreshLook(0f);
            m_Root.gameObject.SetActive(visible);
        }

        void Clear()
        {
            foreach (var e in m_Entries)
            {
                if (e.Preview != null) { e.Preview.Release(); Destroy(e.Preview); }
                Destroy(e.PanelMat); Destroy(e.FrameMat); Destroy(e.ProgressMat); Destroy(e.IconMat);
                Destroy(e.ProgressMesh);
            }
            m_Entries.Clear();
            if (m_Root != null)
            {
                // Destroy is deferred to the end of the frame; hide it now so it cannot leak into the new previews.
                m_Root.gameObject.SetActive(false);
                Destroy(m_Root.gameObject);
            }
            m_Root = null;
            Destroy(m_DiscMesh); Destroy(m_RingMesh); Destroy(m_IconMesh); Destroy(m_PlateMesh);
            m_Hovered = -1;
            m_Dwell = 0f;
        }

        Entry CreateEntry(string label, int characterIndex, Vector3 position)
        {
            var e = new Entry { Label = label, CharacterIndex = characterIndex };
            e.Root = new GameObject("Portal " + label).transform;
            e.Root.SetParent(m_Root, false);
            e.Root.position = position;

            e.PanelMat = MakeLit(panelColor, Color.black, 0.55f);
            e.FrameMat = MakeLit(frameColor * 0.5f, frameColor * frameGlow, 0.5f);
            e.ProgressMat = MakeLit(hoverColor * 0.3f, hoverColor, 0.3f);
            e.IconMat = MakeUnlit(null);

            e.Panel = MakePart(e.Root, "Panel", m_DiscMesh, e.PanelMat, 0f);
            e.Icon = MakePart(e.Root, "Icon", m_IconMesh, e.IconMat, 0.002f);
            e.Frame = MakePart(e.Root, "Frame", m_RingMesh, e.FrameMat, 0.004f);
            e.ProgressMesh = new Mesh { name = "Progress" };
            e.Progress = MakePart(e.Root, "Progress", e.ProgressMesh, e.ProgressMat, 0.006f);
            e.Progress.enabled = false;

            if (m_Font != null)
            {
                // A dark plate keeps the label readable against the moon or a bright projector wall.
                var plate = MakePart(e.Root, "Label plate", m_PlateMesh, e.PanelMat, -0.002f);
                plate.transform.localPosition = new Vector3(0f, -radius - 0.03f - 0.075f * 0.5f, -0.002f);

                var textGo = new GameObject("Label");
                textGo.transform.SetParent(e.Root, false);
                textGo.transform.localPosition = new Vector3(0f, -radius - 0.03f, 0f);
                // The main camera looks down -Z; a TextMesh reads correctly from the other side, so turn it around.
                textGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                e.Text = textGo.AddComponent<TextMesh>();
                e.Text.font = m_Font;
                e.Text.text = label;
                e.Text.fontSize = 64;
                e.Text.characterSize = 0.0075f; // about 5 cm letters (world height ~ characterSize * fontSize / 10)
                e.Text.anchor = TextAnchor.UpperCenter;
                e.Text.alignment = TextAlignment.Center;
                e.Text.color = frameColor;
                var mr = textGo.GetComponent<MeshRenderer>();
                mr.sharedMaterial = m_Font.material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return e;
        }

        static MeshRenderer MakePart(Transform parent, string name, Mesh mesh, Material mat, float z)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        static Material MakeLit(Color baseColor, Color emission, float smoothness)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", baseColor);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor("_EmissionColor", emission);
            return m;
        }

        static Material MakeUnlit(Texture tex)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.SetColor("_BaseColor", Color.white);
            if (tex != null) m.SetTexture("_BaseMap", tex);
            return m;
        }

        // ---------------------------------------------------------------- previews

        /// <summary>Render each entry (standing bones, each rig in its rest pose) into a small texture for its portal.</summary>
        void RenderPreviews()
        {
            var skeleton = m_Director.skeletonView;
            var charRoot = m_Director.characterRoot;
            var slots = m_Director.Slots;

            var camGo = new GameObject("MenuPreviewCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = panelColor;
            cam.fieldOfView = 24f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            var urp = cam.GetUniversalAdditionalCameraData();
            urp.renderPostProcessing = false;
            urp.antialiasing = AntialiasingMode.None;

            // Remember what is showing, then stage one thing at a time in front of the preview camera.
            var skelActive = skeleton != null && skeleton.gameObject.activeSelf;
            var skelPos = skeleton != null ? skeleton.transform.localPosition : Vector3.zero;
            var rootActive = charRoot != null && charRoot.gameObject.activeSelf;
            var rootPos = charRoot != null ? charRoot.localPosition : Vector3.zero;
            var slotActive = new bool[slots.Length];
            for (var i = 0; i < slots.Length; i++) slotActive[i] = slots[i] != null && slots[i].gameObject.activeSelf;
            m_Root.gameObject.SetActive(false);
            var performer2 = m_Director.Performer2Root;
            var performer2Active = performer2 != null && performer2.gameObject.activeSelf;
            if (performer2 != null) performer2.gameObject.SetActive(false);

            var w = Mathf.Max(32, previewSize);
            var h = w;
            foreach (var e in m_Entries)
            {
                if (skeleton != null) skeleton.gameObject.SetActive(false);
                if (charRoot != null) charRoot.gameObject.SetActive(false);
                Bounds bounds;
                var ok = false;
                if (e.CharacterIndex < 0)
                {
                    if (skeleton == null) continue;
                    skeleton.gameObject.SetActive(true);
                    skeleton.transform.localPosition = Vector3.zero;
                    StandingPose.Fill(m_Standing);
                    skeleton.ApplyImmediate(m_Standing);
                    ok = skeleton.TryGetBounds(out bounds);
                }
                else
                {
                    if (charRoot == null || e.CharacterIndex >= slots.Length || slots[e.CharacterIndex] == null) continue;
                    charRoot.gameObject.SetActive(true);
                    charRoot.localPosition = Vector3.zero;
                    for (var i = 0; i < slots.Length; i++) if (slots[i] != null) slots[i].gameObject.SetActive(i == e.CharacterIndex);
                    // Renderer bounds include the mesh (chibi rigs carry huge skulls far above the Head bone); bones are the fallback.
                    ok = TryGetRendererBounds(slots[e.CharacterIndex].gameObject, out bounds) || TryGetBoneBounds(slots[e.CharacterIndex].Animator, out bounds);
                }
                if (!ok) continue;

                e.Preview = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32) { name = "Preview " + e.Label };
                cam.targetTexture = e.Preview;
                cam.aspect = w / (float)h;
                FrameBounds(cam, bounds, w / (float)h);
                if (logPreviewFraming)
                    Debug.Log($"[MimicMocap] Preview '{e.Label}': bounds centre {bounds.center} size {bounds.size}, camera at {cam.transform.position}, fov {cam.fieldOfView}, aspect {cam.aspect}");
                cam.Render();
                cam.targetTexture = null;
                e.IconMat.SetTexture("_BaseMap", e.Preview);
            }

            // Put the stage back.
            if (skeleton != null) { skeleton.transform.localPosition = skelPos; skeleton.gameObject.SetActive(skelActive); }
            if (charRoot != null)
            {
                charRoot.localPosition = rootPos;
                for (var i = 0; i < slots.Length; i++) if (slots[i] != null) slots[i].gameObject.SetActive(slotActive[i]);
                charRoot.gameObject.SetActive(rootActive);
            }
            if (performer2 != null) performer2.gameObject.SetActive(performer2Active);
            m_Root.gameObject.SetActive(visible);
            Destroy(camGo);
        }

        /// <summary>Bounds of a humanoid's bone positions, padded for the skull, soles and limb thickness.</summary>
        static bool TryGetBoneBounds(Animator animator, out Bounds bounds)
        {
            bounds = default;
            if (animator == null || !animator.isHuman) return false;
            var any = false;
            for (var b = 0; b < (int)HumanBodyBones.LastBone; b++)
            {
                var t = animator.GetBoneTransform((HumanBodyBones)b);
                if (t == null) continue;
                if (!any) { bounds = new Bounds(t.position, Vector3.zero); any = true; }
                else bounds.Encapsulate(t.position);
            }
            if (!any) return false;
            var height = Mathf.Max(0.5f, bounds.size.y);
            var min = bounds.min; var max = bounds.max;
            min.y -= height * 0.04f;
            max.y += height * 0.14f; // the Head bone sits at the skull base
            min.x -= height * 0.06f; max.x += height * 0.06f;
            min.z -= height * 0.06f; max.z += height * 0.06f;
            bounds.SetMinMax(min, max);
            return true;
        }

        static bool TryGetRendererBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            var any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        /// <summary>
        /// Place the camera in front (+Z) of the bounds so the figure fills the frame vertically. The portal shows the
        /// inscribed circle, so a wide T-pose is allowed to lose its fingertips rather than shrink the whole figure.
        /// </summary>
        static void FrameBounds(Camera cam, Bounds b, float aspect)
        {
            var halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            var halfHeight = Mathf.Max(b.extents.y * 1.02f, b.extents.x / aspect * 0.62f);
            var distance = halfHeight / Mathf.Tan(halfFov) + b.extents.z;
            cam.transform.position = new Vector3(b.center.x, b.center.y, b.center.z + distance);
            cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        }

        // ---------------------------------------------------------------- interaction

        void Update()
        {
            if (m_Dirty) Rebuild();
            if (m_Root == null) return;
            if (m_Root.gameObject.activeSelf != visible) m_Root.gameObject.SetActive(visible);
            if (!visible)
            {
                m_Hovered = -1;
                m_Dwell = 0f;
                return;
            }

            CollectHands();
            var hovered = -1;
            var hoverPerformer = 0;
            for (var i = 0; i < m_Entries.Count && hovered < 0; i++)
                foreach (var hand in m_Hands)
                    if (Inside(m_Entries[i], hand.Position)) { hovered = i; hoverPerformer = hand.Performer; break; }

            if (hovered != m_Hovered || hoverPerformer != m_HoverPerformer)
            {
                m_Hovered = hovered;
                m_HoverPerformer = hoverPerformer;
                m_Dwell = 0f;
                m_Armed = true; // moving to another portal (or leaving) arms the next press
            }
            else if (hovered >= 0)
            {
                m_Dwell += Time.deltaTime;
            }

            var now = Time.time;
            if (hovered >= 0 && m_Armed && now >= m_CooldownUntil && m_Dwell >= dwellSeconds)
            {
                Press(hovered);
                m_Armed = false;
                m_CooldownUntil = now + cooldownSeconds;
                m_Dwell = 0f;
            }

            RefreshLook(Time.deltaTime);
        }

        void Press(int index)
        {
            var e = m_Entries[index];
            e.Flash = 1f;
            // Whoever reached up gets the new look; the other performer keeps theirs.
            m_Director.SelectCharacterFor(Mathf.Max(1, m_HoverPerformer), e.CharacterIndex);
            EntryPressed?.Invoke(index);
        }

        bool Inside(Entry e, Vector3 p)
        {
            var d = p - e.Root.position;
            var r = radius + hoverMargin;
            return d.x * d.x + d.y * d.y <= r * r && Mathf.Abs(d.z) <= depthTolerance;
        }

        /// <summary>World positions of the avatar's hands, from whichever visual is showing.</summary>
        void CollectHands()
        {
            m_Hands.Clear();
            if (TestHandOverride.HasValue)
            {
                m_Hands.Add(new HandPoint { Position = TestHandOverride.Value, Performer = Mathf.Clamp(TestHandPerformer, 1, 2) });
                return;
            }

            if (m_Director.SmoothedPose.Tracked)
            {
                var charRoot = m_Director.characterRoot;
                if (charRoot != null && charRoot.gameObject.activeInHierarchy && m_Director.CharacterReady && m_Director.CurrentSlot != null)
                {
                    var animator = m_Director.CurrentSlot.Animator;
                    if (animator != null)
                    {
                        AddBoneHand(animator, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, 1);
                        AddBoneHand(animator, HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, 1);
                    }
                }
                var view = m_Director.skeletonView;
                if (view != null && view.gameObject.activeInHierarchy)
                {
                    AddSkeletonHand(view, HumanJoint.LeftWrist, HumanJoint.LeftElbow, 1);
                    AddSkeletonHand(view, HumanJoint.RightWrist, HumanJoint.RightElbow, 1);
                }
            }

            if (m_Director.Performer2Active && m_Director.SmoothedPose2.Tracked)
            {
                var animator2 = m_Director.Performer2Animator;
                if (m_Director.Performer2ShowsCharacter && animator2 != null && animator2.gameObject.activeInHierarchy)
                {
                    AddBoneHand(animator2, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, 2);
                    AddBoneHand(animator2, HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, 2);
                }
                var view2 = m_Director.SkeletonView2;
                if (view2 != null && view2.gameObject.activeInHierarchy)
                {
                    AddSkeletonHand(view2, HumanJoint.LeftWrist, HumanJoint.LeftElbow, 2);
                    AddSkeletonHand(view2, HumanJoint.RightWrist, HumanJoint.RightElbow, 2);
                }
            }
        }

        void AddBoneHand(Animator animator, HumanBodyBones hand, HumanBodyBones knuckle, int performer)
        {
            var h = animator.GetBoneTransform(hand);
            if (h == null) return;
            var k = animator.GetBoneTransform(knuckle);
            // Palm centre: a bit past the wrist toward the knuckles.
            m_Hands.Add(new HandPoint { Position = k != null ? Vector3.Lerp(h.position, k.position, 0.6f) : h.position, Performer = performer });
        }

        void AddSkeletonHand(ProceduralSkeletonView view, HumanJoint wrist, HumanJoint elbow, int performer)
        {
            if (!view.IsJointVisible(wrist)) return;
            var w = view.GetJointWorldPosition(wrist);
            if (view.IsJointVisible(elbow))
            {
                var dir = w - view.GetJointWorldPosition(elbow);
                if (dir.sqrMagnitude > 1e-6f) w += dir.normalized * 0.09f;
            }
            m_Hands.Add(new HandPoint { Position = w, Performer = performer });
        }

        void RefreshLook(float dt)
        {
            var selected = SelectedEntry;
            var selected2 = SelectedEntry2;
            for (var i = 0; i < m_Entries.Count; i++)
            {
                var e = m_Entries[i];
                var isHovered = i == m_Hovered;
                var fraction = isHovered ? DwellFraction : 0f;
                e.Hover = Mathf.MoveTowards(e.Hover, isHovered ? 1f : 0f, dt * 6f);
                e.Flash = Mathf.MoveTowards(e.Flash, 0f, dt * 3f);

                var frame = i == selected && i == selected2 ? Color.Lerp(selectedColor, selected2Color, 0.5f)
                    : i == selected ? selectedColor
                    : i == selected2 ? selected2Color
                    : frameColor * frameGlow;
                frame = Color.Lerp(frame, hoverColor, e.Hover * 0.6f);
                e.FrameMat.SetColor("_EmissionColor", frame + Color.white * (e.Flash * 1.5f));
                e.PanelMat.SetColor("_EmissionColor", hoverColor * (0.12f * e.Hover) + Color.white * (e.Flash * 0.6f));
                e.IconMat.SetColor("_BaseColor", Color.Lerp(new Color(0.8f, 0.8f, 0.8f), Color.white, Mathf.Max(e.Hover, i == selected ? 1f : 0f)));

                var showProgress = isHovered && fraction > 0.01f;
                if (showProgress) BuildArc(e.ProgressMesh, radius * 0.9f, radius * 1.08f, fraction, 72);
                e.Progress.enabled = showProgress;
                e.Root.localScale = Vector3.one * (1f + 0.08f * e.Hover + 0.15f * e.Flash);
            }
        }

        // ---------------------------------------------------------------- meshes

        /// <summary>
        /// A disc (inner 0) or annulus sector in the XY plane, normal +Z, spanning <paramref name="fraction"/> of a turn
        /// from the top. It sweeps toward -X, which is clockwise for the audience (the camera looks down -Z).
        /// </summary>
        static Mesh BuildArc(Mesh mesh, float inner, float outer, float fraction, int segments)
        {
            mesh ??= new Mesh();
            mesh.Clear();
            fraction = Mathf.Clamp01(fraction);
            var n = Mathf.Max(1, Mathf.CeilToInt(segments * fraction));
            var sweep = fraction * Mathf.PI * 2f;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            if (inner <= 1e-5f)
            {
                verts.Add(Vector3.zero);
                for (var i = 0; i <= n; i++)
                {
                    var a = sweep * i / n;
                    verts.Add(new Vector3(-Mathf.Sin(a) * outer, Mathf.Cos(a) * outer, 0f));
                }
                for (var i = 0; i < n; i++) { tris.Add(0); tris.Add(1 + i); tris.Add(2 + i); }
            }
            else
            {
                for (var i = 0; i <= n; i++)
                {
                    var a = sweep * i / n;
                    var s = -Mathf.Sin(a); var c = Mathf.Cos(a);
                    verts.Add(new Vector3(s * inner, c * inner, 0f));
                    verts.Add(new Vector3(s * outer, c * outer, 0f));
                }
                for (var i = 0; i < n; i++)
                {
                    var a0 = i * 2; var b0 = a0 + 1; var a1 = a0 + 2; var b1 = a0 + 3;
                    tris.Add(a0); tris.Add(b0); tris.Add(b1);
                    tris.Add(a0); tris.Add(b1); tris.Add(a1);
                }
            }
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            var normals = new Vector3[verts.Count];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.forward;
            mesh.SetNormals(normals);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// A disc facing +Z that shows the inscribed circle of a square texture. The scene camera looks down -Z,
        /// where +X is on the audience's left, so u runs the other way to keep the picture unmirrored.
        /// </summary>
        static Mesh BuildIconDisc(float r, int segments)
        {
            var mesh = new Mesh { name = "Icon" };
            var verts = new List<Vector3> { Vector3.zero };
            var uvs = new List<Vector2> { new Vector2(0.5f, 0.5f) };
            for (var i = 0; i <= segments; i++)
            {
                var a = Mathf.PI * 2f * i / segments;
                var x = -Mathf.Sin(a) * r;
                var y = Mathf.Cos(a) * r;
                verts.Add(new Vector3(x, y, 0f));
                uvs.Add(new Vector2(0.5f - x / (2f * r), 0.5f + y / (2f * r)));
            }
            var tris = new List<int>();
            for (var i = 0; i < segments; i++) { tris.Add(0); tris.Add(1 + i); tris.Add(2 + i); }
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            var normals = new Vector3[verts.Count];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.forward;
            mesh.SetNormals(normals);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A plain rectangle facing +Z (label backing).</summary>
        static Mesh BuildPlate(float width, float height)
        {
            var w = width * 0.5f; var h = height * 0.5f;
            var mesh = new Mesh { name = "Plate" };
            mesh.SetVertices(new[] { new Vector3(-w, -h, 0f), new Vector3(-w, h, 0f), new Vector3(w, h, 0f), new Vector3(w, -h, 0f) });
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            mesh.SetNormals(new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward });
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
