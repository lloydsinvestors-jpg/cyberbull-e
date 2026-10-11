// Bone-for-bone retargeting: Schedule I's animated skeleton -> Jimmy's Bully skeleton.
// Why hand-rolled: Schedule I's build stripped Unity's AvatarBuilder and HumanPoseHandler.SetHumanPose; Il2CppInterop's
// rebuilt stand-ins hand the engine garbage (v0.2.0 crashed on load trying to allocate 464 GB). This only uses
// Transform math plus Animator.GetBoneTransform, which the game itself ships.
//
// How: both skeletons are put into the same T-pose once (arms straight out, legs straight down, spine straight up;
// done by swinging each bone so it points at its child along the target direction). After that, every frame each of
// Jimmy's bones turns by exactly what the matching Schedule I bone turned away from ITS T-pose:
//     jimmyWorld = siWorld * inverse(siT) * jimmyT      (all T rotations kept relative to each character's root)
using System.Collections.Generic;
using UnityEngine;

namespace BullyLink
{
    public class Retarget
    {
        // Unity humanoid slot -> Bully bone. Hips is Bully's "Root": the spine hangs off Root (via Root01), a sibling
        // of Root Pelvis, so Root is the only bone above both legs and spine.
        public static readonly (HumanBodyBones hb, string bully)[] Pairs =
        {
            (HumanBodyBones.Hips, "Root"),
            (HumanBodyBones.Spine, "Root Spine"), (HumanBodyBones.Chest, "Root Spine1"), (HumanBodyBones.UpperChest, "Root Spine2"),
            (HumanBodyBones.Neck, "Root Neck"), (HumanBodyBones.Head, "Root Head"),
            (HumanBodyBones.LeftUpperLeg, "Root L Thigh"), (HumanBodyBones.LeftLowerLeg, "Root L Calf"), (HumanBodyBones.LeftFoot, "Root L Foot"),
            (HumanBodyBones.RightUpperLeg, "Root R Thigh"), (HumanBodyBones.RightLowerLeg, "Root R Calf"), (HumanBodyBones.RightFoot, "Root R Foot"),
            (HumanBodyBones.LeftShoulder, "Root L Clavicle"), (HumanBodyBones.LeftUpperArm, "Root L UpperArm"),
            (HumanBodyBones.LeftLowerArm, "Root L Forearm"), (HumanBodyBones.LeftHand, "Root L Hand"),
            (HumanBodyBones.RightShoulder, "Root R Clavicle"), (HumanBodyBones.RightUpperArm, "Root R UpperArm"),
            (HumanBodyBones.RightLowerArm, "Root R Forearm"), (HumanBodyBones.RightHand, "Root R Hand"),
        };

        // T-pose: (bone, the bone it should point at, direction in the character's own space). Parent before child.
        static readonly (HumanBodyBones bone, HumanBodyBones[] toward, Vector3 dir)[] TPose =
        {
            (HumanBodyBones.Spine, new[] { HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck }, Vector3.up),
            (HumanBodyBones.Chest, new[] { HumanBodyBones.UpperChest, HumanBodyBones.Neck }, Vector3.up),
            (HumanBodyBones.UpperChest, new[] { HumanBodyBones.Neck }, Vector3.up),
            (HumanBodyBones.Neck, new[] { HumanBodyBones.Head }, Vector3.up),
            (HumanBodyBones.LeftShoulder, new[] { HumanBodyBones.LeftUpperArm }, Vector3.left),
            (HumanBodyBones.LeftUpperArm, new[] { HumanBodyBones.LeftLowerArm }, Vector3.left),
            (HumanBodyBones.LeftLowerArm, new[] { HumanBodyBones.LeftHand }, Vector3.left),
            (HumanBodyBones.RightShoulder, new[] { HumanBodyBones.RightUpperArm }, Vector3.right),
            (HumanBodyBones.RightUpperArm, new[] { HumanBodyBones.RightLowerArm }, Vector3.right),
            (HumanBodyBones.RightLowerArm, new[] { HumanBodyBones.RightHand }, Vector3.right),
            (HumanBodyBones.LeftUpperLeg, new[] { HumanBodyBones.LeftLowerLeg }, Vector3.down),
            (HumanBodyBones.LeftLowerLeg, new[] { HumanBodyBones.LeftFoot }, Vector3.down),
            (HumanBodyBones.RightUpperLeg, new[] { HumanBodyBones.RightLowerLeg }, Vector3.down),
            (HumanBodyBones.RightLowerLeg, new[] { HumanBodyBones.RightFoot }, Vector3.down),
        };

        readonly Transform _siRoot, _jRoot;
        readonly List<(Transform si, Transform j, Quaternion siTInv, Quaternion jT)> _links = new();
        readonly Transform _siHips, _jHips;
        readonly float _scale;
        public int Count => _links.Count;

        public Retarget(Transform siRoot, Dictionary<HumanBodyBones, Transform> si, Transform jRoot, Dictionary<HumanBodyBones, Transform> j)
        {
            _siRoot = siRoot;
            _jRoot = jRoot;
            var shared = new Dictionary<HumanBodyBones, (Transform, Transform)>();
            foreach (var kv in si)
                if (kv.Value != null && j.TryGetValue(kv.Key, out var jt) && jt != null) shared[kv.Key] = (kv.Value, jt);
            var siT = CalibrateT(siRoot, Pick(shared, 0));
            var jT = CalibrateT(jRoot, Pick(shared, 1));
            foreach (var (hb, _) in Pairs)                       // Pairs is parent-before-child
                if (shared.TryGetValue(hb, out var pr))
                    _links.Add((pr.Item1, pr.Item2, Quaternion.Inverse(siT[hb]), jT[hb]));
            _siHips = si[HumanBodyBones.Hips];
            _jHips = j[HumanBodyBones.Hips];
            float siH = siRoot.InverseTransformPoint(_siHips.position).y, jH = jRoot.InverseTransformPoint(_jHips.position).y;
            _scale = siH > 0.1f ? jH / siH : 1f;
        }

        static Dictionary<HumanBodyBones, Transform> Pick(Dictionary<HumanBodyBones, (Transform, Transform)> m, int which)
        {
            var d = new Dictionary<HumanBodyBones, Transform>();
            foreach (var kv in m) d[kv.Key] = which == 0 ? kv.Value.Item1 : kv.Value.Item2;
            return d;
        }

        /// <summary>Swing the skeleton into a T-pose, record each bone's rotation relative to the character root, put it back.</summary>
        static Dictionary<HumanBodyBones, Quaternion> CalibrateT(Transform root, Dictionary<HumanBodyBones, Transform> b)
        {
            var saved = new Dictionary<Transform, Quaternion>();
            foreach (var t in b.Values) saved[t] = t.localRotation;
            foreach (var (bone, toward, dir) in TPose)
            {
                if (!b.TryGetValue(bone, out var t)) continue;
                Transform child = null;
                foreach (var c in toward)
                    if (b.TryGetValue(c, out child)) break;
                if (child == null) continue;
                var cur = child.position - t.position;
                if (cur.sqrMagnitude < 1e-8f) continue;
                var want = root.TransformDirection(dir);
                t.rotation = Quaternion.FromToRotation(cur, want) * t.rotation;
            }
            var inv = Quaternion.Inverse(root.rotation);
            var res = new Dictionary<HumanBodyBones, Quaternion>();
            foreach (var kv in b) res[kv.Key] = inv * kv.Value.rotation;
            foreach (var kv in saved) kv.Key.localRotation = kv.Value;
            return res;
        }

        /// <summary>Call in LateUpdate, after Schedule I's animator has posed its skeleton.</summary>
        public void Apply() => Apply(1f, true);

        /// <summary>weight &lt; 1 blends over whatever the destination already has this frame (NPC reactions fade in/out);
        /// moveRoot=false leaves the destination's root where it is (an NPC keeps its own placement).</summary>
        public void Apply(float weight, bool moveRoot)
        {
            if (moveRoot) _jRoot.SetPositionAndRotation(_siRoot.position, _siRoot.rotation);
            var siRootRot = _siRoot.rotation;
            var jRootRot = _jRoot.rotation;
            foreach (var (si, j, siTInv, jT) in _links)
            {
                var q = si.rotation * siTInv * Quaternion.Inverse(siRootRot) * jRootRot * jT;
                j.rotation = weight >= 1f ? q : Quaternion.Slerp(j.rotation, q, weight);
            }
            // hips height/sway, scaled to the destination's leg length
            var hp = _jRoot.TransformPoint(_siRoot.InverseTransformPoint(_siHips.position) * _scale);
            _jHips.position = weight >= 1f ? hp : Vector3.Lerp(_jHips.position, hp, weight);
        }
    }
}
