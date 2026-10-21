using System.Collections.Generic;
using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// A glowing anatomical skeleton driven straight from joint positions: knobbed long bones, a vertebral column,
    /// curved ribs with a sternum, a pelvis, a skull with sockets, nose and grin, and 21-point hands when tracked.
    /// Needs no character asset, so it always works. Feet are planted on y=0 of this transform.
    /// </summary>
    public sealed class ProceduralSkeletonView : MonoBehaviour
    {
        [Header("Look")]
        public Material boneMaterial;
        public Material jointMaterial;
        public Material skullMaterial;
        public Material socketMaterial;
        [Range(0.01f, 0.1f)] public float boneRadius = 0.032f;
        [Range(0.01f, 0.15f)] public float jointRadius = 0.034f;
        [Range(0.05f, 0.3f)] public float skullRadius = 0.12f;
        [Tooltip("Ribcage, vertebrae, pelvis, skull details. Off = plain glowing stick skeleton.")]
        public bool anatomicalDetails = true;
        [Range(3, 8)] public int ribPairs = 6;
        [Range(4, 12)] public int vertebraCount = 8;
        [Tooltip("Draw 21-point hand bones when the hand tracker provides them (replaces the wrist fork).")]
        public bool drawFingers = true;
        [Range(0.004f, 0.03f)] public float fingerRadius = 0.0085f;

        [Header("Placement")]
        [Tooltip("Target hip height in metres for an average standing subject.")]
        public float standingHipHeight = 0.95f;
        public bool applyTravel = true;
        public float travelScale = 1f;
        [Tooltip("Metres the skeleton may wander from the origin sideways or in depth.")]
        public float maxTravel = 1.5f;
        /// <summary>Subject-space metres of jump (PoseFrame.Lift) applied at most.</summary>
        public float maxLift = 1.2f;
        [Range(0f, 1f)] public float confidenceFloor = 0.35f;
        [Tooltip("Seconds without tracking before the skeleton fades out.")]
        public float fadeOutDelay = 1.5f;

        // Body
        readonly List<Transform> m_Bones = new List<Transform>();
        readonly Transform[] m_Joints = new Transform[JointTopology.Count];
        readonly Vector3[] m_Positions = new Vector3[JointTopology.Count];
        readonly bool[] m_Visible = new bool[JointTopology.Count];

        // Skull
        Transform m_Skull, m_LeftSocket, m_RightSocket, m_Nasal, m_Jaw;
        readonly List<Transform> m_Teeth = new List<Transform>();

        // Torso anatomy
        readonly List<Transform> m_LeftRibs = new List<Transform>();
        readonly List<Transform> m_RightRibs = new List<Transform>();
        readonly List<Transform> m_Vertebrae = new List<Transform>();
        Transform m_Sternum, m_Sacrum, m_LeftIliac, m_RightIliac;

        // Hands
        readonly List<Transform> m_LeftFingerBones = new List<Transform>();
        readonly List<Transform> m_RightFingerBones = new List<Transform>();
        static readonly HumanJoint[] k_LeftForkJoints = { HumanJoint.LeftHandTip, HumanJoint.LeftThumb, HumanJoint.LeftPinky };
        static readonly HumanJoint[] k_RightForkJoints = { HumanJoint.RightHandTip, HumanJoint.RightThumb, HumanJoint.RightPinky };

        static readonly HashSet<(HumanJoint, HumanJoint)> k_SpineBones = new HashSet<(HumanJoint, HumanJoint)>
        {
            (HumanJoint.Pelvis, HumanJoint.Spine), (HumanJoint.Spine, HumanJoint.Chest), (HumanJoint.Chest, HumanJoint.Neck),
        };

        float m_LastTrackedTime = -100f;
        bool m_Built;
        float m_Opacity = 1f;
        Renderer[] m_Renderers;
        MaterialPropertyBlock m_Block;
        static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");
        Color m_BoneEmission = Color.white;
        Color m_JointEmission = Color.white;

        void Awake() => Build();

        /// <summary>World position of a joint as last drawn (for cameras and tests).</summary>
        public Vector3 GetJointWorldPosition(HumanJoint joint) => transform.TransformPoint(m_Positions[(int)joint]);
        public bool IsJointVisible(HumanJoint joint) => m_Visible[(int)joint];

        public void Build()
        {
            if (m_Built) return;
            m_Built = true;
            m_Block = new MaterialPropertyBlock();

            foreach (var (a, b) in JointTopology.Bones)
            {
                var small = a == HumanJoint.LeftWrist || a == HumanJoint.RightWrist || a == HumanJoint.LeftAnkle || a == HumanJoint.RightAnkle;
                m_Bones.Add(MakeMeshPart(small ? SkeletonMeshes.SlimBone : SkeletonMeshes.Bone, $"Bone_{a}_{b}", boneMaterial, transform));
            }

            for (var i = 0; i < JointTopology.Count; i++)
            {
                var j = (HumanJoint)i;
                if (j == HumanJoint.Nose || j == HumanJoint.LeftEye || j == HumanJoint.RightEye || j == HumanJoint.LeftEar || j == HumanJoint.RightEar || j == HumanJoint.Head) continue;
                if (anatomicalDetails && (j == HumanJoint.Spine || j == HumanJoint.Chest || j == HumanJoint.LeftIndex || j == HumanJoint.RightIndex)) continue;
                m_Joints[i] = MakePart(PrimitiveType.Sphere, $"Joint_{j}", jointMaterial, transform);
            }

            m_Skull = MakePart(PrimitiveType.Sphere, "Skull", skullMaterial, transform);
            if (anatomicalDetails)
            {
                m_LeftSocket = MakePart(PrimitiveType.Sphere, "EyeSocketL", socketMaterial, m_Skull);
                m_RightSocket = MakePart(PrimitiveType.Sphere, "EyeSocketR", socketMaterial, m_Skull);
                m_Nasal = MakePart(PrimitiveType.Sphere, "Nasal", socketMaterial, m_Skull);
                m_Jaw = MakePart(PrimitiveType.Sphere, "Jaw", skullMaterial, m_Skull);
                m_LeftSocket.localPosition = new Vector3(-0.19f, 0.06f, 0.42f);
                m_RightSocket.localPosition = new Vector3(0.19f, 0.06f, 0.42f);
                m_LeftSocket.localScale = Vector3.one * 0.27f;
                m_RightSocket.localScale = Vector3.one * 0.27f;
                m_Nasal.localPosition = new Vector3(0f, -0.13f, 0.47f);
                m_Nasal.localScale = new Vector3(0.11f, 0.17f, 0.10f);
                m_Jaw.localPosition = new Vector3(0f, -0.40f, 0.14f);
                m_Jaw.localScale = new Vector3(0.62f, 0.34f, 0.62f);
                for (var i = 0; i < 6; i++)
                {
                    var tooth = MakePart(PrimitiveType.Cube, "Tooth" + i, skullMaterial, m_Skull);
                    var x = Mathf.Lerp(-0.17f, 0.17f, i / 5f);
                    tooth.localPosition = new Vector3(x, -0.29f, 0.44f - Mathf.Abs(x) * 0.35f);
                    tooth.localScale = new Vector3(0.05f, 0.09f, 0.04f);
                    m_Teeth.Add(tooth);
                }

                for (var i = 0; i < ribPairs; i++)
                {
                    m_RightRibs.Add(MakeMeshPart(SkeletonMeshes.Rib, "RibR" + i, boneMaterial, transform));
                    m_LeftRibs.Add(MakeMeshPart(SkeletonMeshes.Rib, "RibL" + i, boneMaterial, transform));
                }
                m_Sternum = MakeMeshPart(SkeletonMeshes.Bone, "Sternum", boneMaterial, transform);
                for (var i = 0; i < vertebraCount; i++)
                    m_Vertebrae.Add(MakeMeshPart(SkeletonMeshes.Vertebra, "Vertebra" + i, boneMaterial, transform));
                m_Sacrum = MakePart(PrimitiveType.Sphere, "Sacrum", boneMaterial, transform);
                m_LeftIliac = MakePart(PrimitiveType.Sphere, "IliacL", boneMaterial, transform);
                m_RightIliac = MakePart(PrimitiveType.Sphere, "IliacR", boneMaterial, transform);
            }

            if (drawFingers)
            {
                for (var i = 0; i < HandFrame.SkeletonBones.Length; i++)
                {
                    m_LeftFingerBones.Add(MakeMeshPart(SkeletonMeshes.SlimBone, "LFinger" + i, boneMaterial, transform));
                    m_RightFingerBones.Add(MakeMeshPart(SkeletonMeshes.SlimBone, "RFinger" + i, boneMaterial, transform));
                }
            }

            m_Renderers = GetComponentsInChildren<Renderer>(true);
            if (boneMaterial != null && boneMaterial.HasProperty(k_EmissionColor)) m_BoneEmission = boneMaterial.GetColor(k_EmissionColor);
            if (jointMaterial != null && jointMaterial.HasProperty(k_EmissionColor)) m_JointEmission = jointMaterial.GetColor(k_EmissionColor);
            SetVisible(false);
        }

        public void Apply(PoseFrame pose)
        {
            if (!m_Built) Build();
            if (!PoseTransforms.HasBody(pose, confidenceFloor)) return;
            m_LastTrackedTime = Time.time;

            // Scale subject to a fixed standing hip height and plant feet at y=0.
            var subjectLeg = Mathf.Max(0.2f, pose.LegLength > 0f ? pose.LegLength : PoseTransforms.LegLength(pose));
            var scale = standingHipHeight / subjectLeg;
            var pelvis = pose[HumanJoint.Pelvis];
            // Stand the figure by the smoother's speed-limited pelvis height when there is one, so a leg glitch dips the feet
            // for a frame instead of dropping the whole body.
            var floorY = pose.HipHeight > 0f ? pelvis.y - pose.HipHeight : PoseTransforms.LowestFootY(pose);
            var travel = applyTravel ? new Vector3(pose.RootOffset.x, 0f, pose.RootOffset.z) * travelScale : Vector3.zero;
            travel.x = Mathf.Clamp(travel.x, -maxTravel, maxTravel);
            travel.z = Mathf.Clamp(travel.z, -maxTravel, maxTravel);
            travel.y = Mathf.Clamp(pose.Lift, 0f, maxLift); // a jump lifts the whole figure off the floor

            for (var i = 0; i < JointTopology.Count; i++)
            {
                var p = pose.Joints[i];
                m_Positions[i] = new Vector3(p.x - pelvis.x, p.y - floorY, p.z - pelvis.z) * scale + travel * scale;
                m_Visible[i] = pose.Confidence[i] >= confidenceFloor;
            }

            // With finger detail, the coarse wrist fork is replaced by the 21-point hand.
            var leftFingers = drawFingers && pose.LeftHand.Tracked && m_Visible[(int)HumanJoint.LeftWrist];
            var rightFingers = drawFingers && pose.RightHand.Tracked && m_Visible[(int)HumanJoint.RightWrist];
            if (leftFingers) foreach (var j in k_LeftForkJoints) m_Visible[(int)j] = false;
            if (rightFingers) foreach (var j in k_RightForkJoints) m_Visible[(int)j] = false;

            for (var i = 0; i < JointTopology.Count; i++)
            {
                var t = m_Joints[i];
                if (t == null) continue;
                t.gameObject.SetActive(m_Visible[i]);
                if (!m_Visible[i]) continue;
                t.localPosition = m_Positions[i];
                var slim = (leftFingers && i == (int)HumanJoint.LeftWrist) || (rightFingers && i == (int)HumanJoint.RightWrist);
                t.localScale = Vector3.one * (jointRadius * (slim ? 1.2f : 2f));
            }

            for (var k = 0; k < JointTopology.Bones.Length; k++)
            {
                var (a, b) = JointTopology.Bones[k];
                var t = m_Bones[k];
                var vis = m_Visible[(int)a] && m_Visible[(int)b] && !(anatomicalDetails && k_SpineBones.Contains((a, b)));
                t.gameObject.SetActive(vis);
                if (!vis) continue;
                // Hand fork and foot bones are slimmer than limbs.
                var slim = a == HumanJoint.LeftWrist || a == HumanJoint.RightWrist || a == HumanJoint.LeftAnkle || a == HumanJoint.RightAnkle;
                PlaceBone(t, m_Positions[(int)a], m_Positions[(int)b], slim ? boneRadius * 0.55f : boneRadius);
            }

            var headVisible = m_Visible[(int)HumanJoint.Head];
            m_Skull.gameObject.SetActive(headVisible);
            if (headVisible)
            {
                m_Skull.localPosition = m_Positions[(int)HumanJoint.Head] + Vector3.up * (skullRadius * 0.35f);
                m_Skull.localScale = new Vector3(skullRadius * 2f, skullRadius * 2.2f, skullRadius * 2.1f);
                var faceDir = HeadForward();
                if (faceDir.sqrMagnitude > 1e-6f) m_Skull.localRotation = Quaternion.LookRotation(faceDir, Vector3.up);
            }

            if (anatomicalDetails) ApplyTorso();
            if (drawFingers)
            {
                ApplyHand(pose.LeftHand, leftFingers, m_Positions[(int)HumanJoint.LeftWrist], scale, m_LeftFingerBones);
                ApplyHand(pose.RightHand, rightFingers, m_Positions[(int)HumanJoint.RightWrist], scale, m_RightFingerBones);
            }
        }

        /// <summary>Place a unit bone mesh (length 1 along Y) between two points.</summary>
        static void PlaceBone(Transform t, Vector3 a, Vector3 b, float radius)
        {
            var dir = b - a;
            var len = dir.magnitude;
            t.localPosition = 0.5f * (a + b);
            if (len > 1e-4f) t.localRotation = Quaternion.FromToRotation(Vector3.up, dir);
            t.localScale = new Vector3(radius * 2f, len, radius * 2f);
        }

        Vector3 HeadForward()
        {
            var lEar = (int)HumanJoint.LeftEar;
            var rEar = (int)HumanJoint.RightEar;
            var lEye = (int)HumanJoint.LeftEye;
            var rEye = (int)HumanJoint.RightEye;
            var nose = (int)HumanJoint.Nose;
            if (m_Visible[lEar] && m_Visible[rEar])
            {
                var mid = 0.5f * (m_Positions[lEar] + m_Positions[rEar]);
                Vector3 d;
                // Eyes sit level with the ears; the nose hangs below them and would tip the skull forward.
                if (m_Visible[lEye] && m_Visible[rEye]) d = 0.5f * (m_Positions[lEye] + m_Positions[rEye]) - mid;
                else if (m_Visible[nose]) { d = m_Positions[nose] - mid; d.y *= 0.5f; }
                else return TorsoForward();
                if (d.sqrMagnitude > 1e-6f)
                {
                    // Damp the head's own turn toward the torso facing so small landmark jitter doesn't swivel the skull.
                    return Vector3.Slerp(TorsoForward(), d.normalized, 0.7f).normalized;
                }
            }
            return TorsoForward();
        }

        Vector3 TorsoForward()
        {
            var right = m_Positions[(int)HumanJoint.RightShoulder] - m_Positions[(int)HumanJoint.LeftShoulder];
            var up = m_Positions[(int)HumanJoint.Neck] - m_Positions[(int)HumanJoint.Pelvis];
            var f = Vector3.Cross(right, up);
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }

        void ApplyTorso()
        {
            var torsoVisible = m_Visible[(int)HumanJoint.Pelvis] && m_Visible[(int)HumanJoint.Neck] && m_Visible[(int)HumanJoint.LeftShoulder] && m_Visible[(int)HumanJoint.RightShoulder];
            foreach (var r in m_LeftRibs) r.gameObject.SetActive(torsoVisible);
            foreach (var r in m_RightRibs) r.gameObject.SetActive(torsoVisible);
            foreach (var v in m_Vertebrae) v.gameObject.SetActive(torsoVisible);
            m_Sternum.gameObject.SetActive(torsoVisible);
            m_Sacrum.gameObject.SetActive(torsoVisible);
            m_LeftIliac.gameObject.SetActive(torsoVisible);
            m_RightIliac.gameObject.SetActive(torsoVisible);
            if (!torsoVisible) return;

            var pelvis = m_Positions[(int)HumanJoint.Pelvis];
            var neck = m_Positions[(int)HumanJoint.Neck];
            var lHip = m_Positions[(int)HumanJoint.LeftHip];
            var rHip = m_Positions[(int)HumanJoint.RightHip];
            var axis = neck - pelvis;
            var torsoLen = axis.magnitude;
            if (torsoLen < 1e-4f) return;
            var up = axis / torsoLen;
            var forward = TorsoForward();
            var right = Vector3.Cross(up, forward).normalized;
            forward = Vector3.Cross(right, up).normalized;
            var torsoRot = Quaternion.LookRotation(forward, up);
            var shoulderWidth = Vector3.Distance(m_Positions[(int)HumanJoint.LeftShoulder], m_Positions[(int)HumanJoint.RightShoulder]);
            var hipWidth = Vector3.Distance(lHip, rHip);

            // Ribs: widest around the middle of the cage, narrowing toward the collar and the waist.
            var n = m_RightRibs.Count;
            var topFrac = 0.90f;
            var bottomFrac = 0.45f;
            Vector3 topFront = Vector3.zero, bottomFront = Vector3.zero;
            for (var i = 0; i < n; i++)
            {
                var t = n > 1 ? i / (float)(n - 1) : 0f;
                var frac = Mathf.Lerp(topFrac, bottomFrac, t);
                var width = shoulderWidth * (0.36f + 0.16f * Mathf.Sin(Mathf.PI * (i + 0.5f) / n));
                var depth = width * 0.62f;
                var centre = pelvis + up * (frac * torsoLen);
                var scaleR = new Vector3(width, width, depth);
                var scaleL = new Vector3(-width, width, depth);
                m_RightRibs[i].localPosition = centre;
                m_RightRibs[i].localRotation = torsoRot;
                m_RightRibs[i].localScale = scaleR;
                m_LeftRibs[i].localPosition = centre;
                m_LeftRibs[i].localRotation = torsoRot;
                m_LeftRibs[i].localScale = scaleL;
                // Front ends of this rib pair (mesh arc ends at 150 deg: x = 0.5, z = 0.866, y = -0.22), for the sternum.
                var front = centre + torsoRot * new Vector3(0f, -0.22f * width, 0.866f * depth);
                if (i == 0) topFront = front;
                if (i == n - 1) bottomFront = front;
            }
            PlaceBone(m_Sternum, topFront + up * 0.02f, bottomFront - up * 0.01f, boneRadius * 0.5f);

            // Vertebrae along the back of the cage, from the sacrum up to the neck.
            var vn = m_Vertebrae.Count;
            for (var i = 0; i < vn; i++)
            {
                var t = vn > 1 ? i / (float)(vn - 1) : 0f;
                var frac = Mathf.Lerp(0.10f, 0.97f, t);
                var width = shoulderWidth * (0.36f + 0.16f * Mathf.Sin(Mathf.PI * Mathf.Clamp01((frac - bottomFrac) / (topFrac - bottomFrac))));
                var back = frac >= bottomFrac ? width * 0.62f * 0.92f : Mathf.Lerp(hipWidth * 0.12f, width * 0.62f * 0.92f, (frac - 0.10f) / (bottomFrac - 0.10f));
                var v = m_Vertebrae[i];
                v.localPosition = pelvis + up * (frac * torsoLen) - forward * back;
                v.localRotation = torsoRot;
                var d = boneRadius * 2.2f;
                v.localScale = new Vector3(d, torsoLen / vn * 0.62f, d * 0.9f);
            }

            // Pelvis: sacrum block and two flared iliac wings.
            m_Sacrum.localPosition = pelvis - forward * (hipWidth * 0.12f);
            m_Sacrum.localRotation = torsoRot;
            m_Sacrum.localScale = new Vector3(hipWidth * 0.55f, boneRadius * 3.2f, hipWidth * 0.32f);
            var flare = Quaternion.AngleAxis(28f, forward);
            m_LeftIliac.localPosition = pelvis - right * (hipWidth * 0.42f) + up * (boneRadius * 1.6f);
            m_LeftIliac.localRotation = torsoRot * flare;
            m_LeftIliac.localScale = new Vector3(hipWidth * 0.30f, hipWidth * 0.50f, hipWidth * 0.20f);
            m_RightIliac.localPosition = pelvis + right * (hipWidth * 0.42f) + up * (boneRadius * 1.6f);
            m_RightIliac.localRotation = torsoRot * Quaternion.Inverse(flare);
            m_RightIliac.localScale = new Vector3(hipWidth * 0.30f, hipWidth * 0.50f, hipWidth * 0.20f);
        }

        void ApplyHand(HandFrame hand, bool visible, Vector3 wrist, float scale, List<Transform> bones)
        {
            foreach (var b in bones) b.gameObject.SetActive(visible);
            if (!visible) return;
            for (var k = 0; k < HandFrame.SkeletonBones.Length; k++)
            {
                var (a, b) = HandFrame.SkeletonBones[k];
                var pa = wrist + hand.Landmarks[a] * scale;
                var pb = wrist + hand.Landmarks[b] * scale;
                // Metacarpals (from the wrist) are a touch thicker than the phalanges.
                var r = a == 0 ? fingerRadius * 1.15f : fingerRadius;
                PlaceBone(bones[k], pa, pb, r);
            }
        }

        void Update()
        {
            var since = Time.time - m_LastTrackedTime;
            var target = since > fadeOutDelay ? 0f : 1f;
            m_Opacity = Mathf.MoveTowards(m_Opacity, target, Time.deltaTime * 2.5f);
            ApplyOpacity();
        }

        /// <summary>Apply a pose and show it at full brightness right away (menu previews, screenshots).</summary>
        public void ApplyImmediate(PoseFrame pose)
        {
            Apply(pose);
            m_Opacity = 1f;
            ApplyOpacity();
        }

        /// <summary>World-space bounds of the visible parts, for framing a preview camera.</summary>
        public bool TryGetBounds(out Bounds bounds)
        {
            bounds = default;
            if (m_Renderers == null) return false;
            var any = false;
            foreach (var r in m_Renderers)
            {
                // Hidden parts are inactive objects whose stale bounds would inflate the figure.
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        void ApplyOpacity()
        {
            SetVisible(m_Opacity > 0.01f);
            if (m_Renderers == null) return;
            foreach (var r in m_Renderers)
            {
                if (r == null || r.sharedMaterial == socketMaterial) continue;
                r.GetPropertyBlock(m_Block);
                var em = r.sharedMaterial == jointMaterial ? m_JointEmission : m_BoneEmission;
                m_Block.SetColor(k_EmissionColor, em * m_Opacity);
                r.SetPropertyBlock(m_Block);
            }
        }

        void SetVisible(bool visible)
        {
            if (m_Renderers == null) return;
            foreach (var r in m_Renderers) if (r != null) r.enabled = visible;
        }

        static Transform MakePart(PrimitiveType type, string name, Material mat, Transform parent)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go.transform;
        }

        static Transform MakeMeshPart(Mesh mesh, string name, Material mat, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (mat != null) mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return go.transform;
        }
    }
}
