using MimicMocap.BlazePose;
using MimicMocap.Maxine;
using NUnit.Framework;
using UnityEngine;

namespace MimicMocap.Tests
{
    public class CoreTests
    {
        [Test]
        public void OneEuro_ConvergesToConstantInput()
        {
            var f = new OneEuroFilter(1f, 0.02f, 1f);
            var v = 0f;
            for (var i = 0; i < 120; i++) v = f.Filter(10f, i / 60.0);
            Assert.That(v, Is.EqualTo(10f).Within(0.05f));
        }

        [Test]
        public void OneEuro_SmoothsJitterButFollowsFastMotion()
        {
            var slow = new OneEuroFilter(1f, 0.0f, 1f);
            var fast = new OneEuroFilter(1f, 0.5f, 1f);
            // Step input: the high-beta filter should get closer to the step sooner.
            float vs = 0f, vf = 0f;
            for (var i = 0; i < 6; i++)
            {
                vs = slow.Filter(i == 0 ? 0f : 1f, i / 60.0);
                vf = fast.Filter(i == 0 ? 0f : 1f, i / 60.0);
            }
            Assert.Greater(vf, vs);
            Assert.Less(vs, 1f);
        }

        [Test]
        public void Topology_EveryJointButPelvisHasParent()
        {
            for (var i = 0; i < JointTopology.Count; i++)
            {
                if ((HumanJoint)i == HumanJoint.Pelvis) Assert.AreEqual(-1, JointTopology.Parent[i]);
                else Assert.GreaterOrEqual(JointTopology.Parent[i], 0, $"{(HumanJoint)i} has no parent");
            }
        }

        [Test]
        public void Layouts_MapIntoCanonicalRange()
        {
            foreach (var c in BlazePoseLayout.ToCanonical) Assert.Less(c, JointTopology.Count);
            foreach (var c in MaxineLayout.ToCanonical) Assert.Less(c, JointTopology.Count);
            Assert.AreEqual((int)HumanJoint.LeftHip, BlazePoseLayout.ToCanonical[(int)BlazeLandmark.LeftHip]);
            Assert.AreEqual((int)HumanJoint.RightWrist, MaxineLayout.ToCanonical[(int)MaxineJoint.RightWrist]);
        }

        [Test]
        public void Mirror_SwapsSidesAndNegatesX()
        {
            var f = new PoseFrame { Tracked = true };
            f[HumanJoint.LeftHip] = new Vector3(-0.1f, 0f, 0f);
            f[HumanJoint.RightHip] = new Vector3(0.1f, 0f, 0f);
            f.Confidence[(int)HumanJoint.LeftHip] = 0.9f;
            f.Confidence[(int)HumanJoint.RightHip] = 0.5f;
            f.RootOffset = new Vector3(0.3f, 0, 0);
            PoseTransforms.Mirror(f);
            Assert.AreEqual(new Vector3(-0.1f, 0, 0), f[HumanJoint.LeftHip]);
            Assert.AreEqual(new Vector3(0.1f, 0, 0), f[HumanJoint.RightHip]);
            Assert.AreEqual(0.5f, f.ConfidenceOf(HumanJoint.LeftHip));
            Assert.AreEqual(-0.3f, f.RootOffset.x);
        }

        [Test]
        public void Smoother_HoldsLowConfidenceJoints()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var a = new PoseFrame { Tracked = true, Timestamp = 0 };
            for (var i = 0; i < JointTopology.Count; i++) { a.Joints[i] = Vector3.one; a.Confidence[i] = 1f; }
            var o = new PoseFrame();
            s.Smooth(a, o);
            var b = new PoseFrame { Tracked = true, Timestamp = 1.0 / 30 };
            a.CopyTo(b);
            b[HumanJoint.LeftWrist] = new Vector3(50, 50, 50);
            b.Confidence[(int)HumanJoint.LeftWrist] = 0.1f; // garbage with low confidence
            s.Smooth(b, o);
            Assert.That(o[HumanJoint.LeftWrist].x, Is.EqualTo(1f).Within(0.01f));
        }

        [Test]
        public void SynthesiseTorso_FillsPelvisAndHeadFromLimbs()
        {
            var f = new PoseFrame { Tracked = true };
            void Set(HumanJoint j, Vector3 v) { f[j] = v; f.Confidence[(int)j] = 1f; }
            Set(HumanJoint.LeftHip, new Vector3(-0.1f, 0f, 0f));
            Set(HumanJoint.RightHip, new Vector3(0.1f, 0f, 0f));
            Set(HumanJoint.LeftShoulder, new Vector3(-0.2f, 0.5f, 0f));
            Set(HumanJoint.RightShoulder, new Vector3(0.2f, 0.5f, 0f));
            f.SynthesiseTorso();
            Assert.That(f[HumanJoint.Pelvis].magnitude, Is.LessThan(1e-4f));
            Assert.That(f[HumanJoint.Neck].y, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.Greater(f[HumanJoint.Head].y, f[HumanJoint.Neck].y);
            Assert.Greater(f.ConfidenceOf(HumanJoint.Head), 0.3f);
        }
    }
}
