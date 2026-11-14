using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Drives a skinned character from the MuJoCo bodies of one fighter: every bone of the scan follows one body.
    /// Render only. The character's rest pose is the physics model's zero pose (tools/make_kim.py bakes that into
    /// the mesh), so a bone keeps the offset it has from its body in that pose. The table of bone, body and their
    /// zero-pose positions (MuJoCo coordinates) comes from koth/build_human.py as &lt;robot&gt;_skin.json (a GLB
    /// character such as Trump's is used in its own rest pose and scaled by the table's "scale").
    /// </summary>
    public class SkinFollower : MonoBehaviour
    {
        [Serializable] class Row { public string bone, body; public float[] bone_pos, body_pos; }
        [Serializable] class Table { public float scale; public Row[] bones; }   // scale: GLB characters keep their source size (tools/make_glb_fighter.py)

        public TextAsset skinJson;
        public string robotPrefix = "a_";
        public Transform skinRoot;                       // the instantiated character (its bones are found by name)

        Transform[] _bone, _body; Vector3[] _posOff; Quaternion[] _rotOff;

        // The spine above the waist, for LifeSigns: the bones on the body's centre line that follow torso_link, by height.
        // The physics model has no neck joint, so the head and neck can turn freely on screen (Look).
        public Transform HeadBone { get; private set; }
        public Transform NeckBone { get; private set; }
        public Transform ChestBone { get; private set; }
        public Transform TorsoBody { get; private set; }
        public Transform PelvisBody { get; private set; }
        /// <summary>Crown of the head above the soles in the rest pose, estimated from the skeleton (m).</summary>
        public float Stature { get; private set; } = 1.7f;
        public bool Ready => _bone != null;
        /// <summary>World-space turn of the head from where the torso carries it (LifeSigns): 40 % at the neck, the rest at the head.</summary>
        [NonSerialized] public Quaternion Look = Quaternion.identity;
        /// <summary>The winner's arms, 0 (where the physics has them) .. 1 (raised in a V). On screen only: the round is
        /// over, the fight brain still balances the body with its arms down (PolicyRunner.PostRound).</summary>
        [NonSerialized] public float ArmsUp;
        public const float ArmsUpPitch = 160f, ArmsUpSpread = 24f;       // degrees: forward and up from hanging, then out
        bool _neckTurns; Transform _armL, _armR;

        static Vector3 Mj(float[] p) => new(p[0], p[2], p[1]);     // MuJoCo x, y, z -> Unity x, z, y

        void Start()
        {
            var table = JsonUtility.FromJson<Table>(skinJson.text); var rows = table.bones;
            skinRoot.localScale = Vector3.one * (table.scale > 0f ? table.scale : 1f);
            var byName = new Dictionary<string, Transform>();
            foreach (var t in skinRoot.GetComponentsInChildren<Transform>(true)) byName[t.name] = t;
            // The importer may leave the character turned about the vertical: take the quarter turn whose rest pose
            // lands on the physics zero pose.
            skinRoot.position = Vector3.zero; float best = float.MaxValue, bestYaw = 0f;
            for (int k = 0; k < 4; k++)
            {
                skinRoot.rotation = Quaternion.Euler(0f, 90f * k, 0f); float err = 0f;
                foreach (var r in rows) err = Mathf.Max(err, Vector3.Distance(byName[r.bone].position, Mj(r.bone_pos)));
                if (err < best) { best = err; bestYaw = 90f * k; }
            }
            skinRoot.rotation = Quaternion.Euler(0f, bestYaw, 0f);
            Debug.Log($"[SkinFollower] {robotPrefix}: rest pose matches the physics zero pose within {best * 1000f:0.0} mm (yaw {bestYaw})");
            if (best > 0.02f) Debug.LogError($"[SkinFollower] {robotPrefix}: the character's rest pose is not the physics zero pose ({best:0.000} m off)");

            int n = rows.Length; _bone = new Transform[n]; _body = new Transform[n]; _posOff = new Vector3[n]; _rotOff = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                _bone[i] = byName[rows[i].bone];
                var go = GameObject.Find(robotPrefix + rows[i].body);
                if (go == null) { Debug.LogError("[SkinFollower] no body " + robotPrefix + rows[i].body); enabled = false; return; }
                _body[i] = go.transform;
                _posOff[i] = _bone[i].position - Mj(rows[i].body_pos);      // body frames are world-aligned in the zero pose
                _rotOff[i] = _bone[i].rotation;
            }
            foreach (var smr in skinRoot.GetComponentsInChildren<SkinnedMeshRenderer>()) smr.updateWhenOffscreen = true;
            FindSpine(rows);
        }

        void FindSpine(Row[] rows)
        {
            var spine = new List<int>(); float top = 0f, low = float.MaxValue;
            for (int i = 0; i < rows.Length; i++)
            {
                top = Mathf.Max(top, rows[i].bone_pos[2]); low = Mathf.Min(low, rows[i].bone_pos[2]);
                if (rows[i].body == "torso_link" && Mathf.Abs(rows[i].bone_pos[1]) < 0.02f) spine.Add(i);
                if (rows[i].body == "torso_link") TorsoBody = _body[i]; else if (rows[i].body == "pelvis") PelvisBody = _body[i];
                // the first bone on each shoulder_yaw_link is the upper arm (rows are parents-first): the forearm and hand hang off it
                else if (rows[i].body == "left_shoulder_yaw_link") _armL ??= _bone[i]; else if (rows[i].body == "right_shoulder_yaw_link") _armR ??= _bone[i];
            }
            spine.Sort((a, b) => rows[a].bone_pos[2].CompareTo(rows[b].bone_pos[2]));
            if (spine.Count < 2 || TorsoBody == null) return;               // no head bone of its own: nothing to turn
            HeadBone = _bone[spine[^1]]; NeckBone = _bone[spine[^2]]; ChestBone = _bone[spine[Mathf.Max(0, spine.Count - 3)]];
            // the head bone sits at the base of the skull: the crown is about 0.9 of a head (13.5 % of the stature) above it
            Stature = (top - Mathf.Max(0f, low - 0.09f)) / (1f - 0.9f * 0.135f);
            // the neck turns only when the head is all it carries (a rig may hang the shoulders off the same bone)
            _neckTurns = HeadBone.IsChildOf(NeckBone);
            for (int i = 0; i < _bone.Length && _neckTurns; i++)
                if (_bone[i] != HeadBone && _bone[i] != NeckBone && _bone[i].IsChildOf(NeckBone) && !_bone[i].IsChildOf(HeadBone)) _neckTurns = false;
        }

        void LateUpdate()
        {
            if (_bone == null) return;
            for (int i = 0; i < _bone.Length; i++)       // rows are parents-first, so children overwrite what a parent moved
                _bone[i].SetPositionAndRotation(_body[i].position + _body[i].rotation * _posOff[i], _body[i].rotation * _rotOff[i]);
            if (ArmsUp > 0.001f && TorsoBody != null && _armL != null && _armR != null)
            {
                // in the torso's frame (x forward, y up, z left): swing about the left axis, then spread about the forward one
                float e = Mathf.Clamp01(ArmsUp); e = e * e * (3f - 2f * e); Quaternion t = TorsoBody.rotation, back = Quaternion.Inverse(t);
                _armL.rotation = t * Quaternion.AngleAxis(ArmsUpPitch * e, Vector3.forward) * Quaternion.AngleAxis(-ArmsUpSpread * e, Vector3.right) * back * _armL.rotation;
                _armR.rotation = t * Quaternion.AngleAxis(ArmsUpPitch * e, Vector3.forward) * Quaternion.AngleAxis(ArmsUpSpread * e, Vector3.right) * back * _armR.rotation;
            }
            if (HeadBone == null || Look == Quaternion.identity) return;
            if (_neckTurns)
            {
                NeckBone.rotation = Quaternion.Slerp(Quaternion.identity, Look, 0.4f) * NeckBone.rotation;       // carries the head with it
                HeadBone.rotation = Quaternion.Slerp(Quaternion.identity, Look, 0.6f) * HeadBone.rotation;
            }
            else HeadBone.rotation = Look * HeadBone.rotation;
        }
    }
}
