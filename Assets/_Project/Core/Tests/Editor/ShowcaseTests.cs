using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace PoKingHill.Tests
{
    /// <summary>The editor-tunable assets exist and say what the rest of the repo says: RewardWeights equals the trainer's
    /// Training/config/reward_penalties.json term by term, the demo idles 30 s, the camera settings are the values the
    /// broadcast camera was tuned with.</summary>
    public class ShowcaseTests
    {
        static float Json(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([-+0-9.eE]+)");
            Assert.IsTrue(m.Success, key + " in reward_penalties.json");
            return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        [Test]
        public void RewardWeightsAssetEqualsTheTrainingConfig()
        {
            var w = Resources.Load<RewardWeights>(RewardWeights.ResourcePath);
            Assert.IsNotNull(w, "Resources/RewardWeights.asset");
            string json = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Training", "config", "reward_penalties.json"));
            Assert.AreEqual(Json(json, "joint_torque"), w.jointTorque, 1e-9f, "joint_torque");
            Assert.AreEqual(Json(json, "ang_vel_xy"), w.angVelXy, 1e-9f, "ang_vel_xy");
            Assert.AreEqual(Json(json, "action_rate"), w.actionRate, 1e-9f, "action_rate");
            Assert.AreEqual(Json(json, "feet_slip"), w.feetSlip, 1e-9f, "feet_slip");
            Assert.AreEqual(Json(json, "joint_vel"), w.jointVel, 1e-9f, "joint_vel");
        }

        [Test]
        public void ShowcaseAssetsAreInResources()
        {
            var demo = Resources.Load<DemoSettings>(DemoSettings.ResourcePath);
            Assert.IsNotNull(demo, "Resources/DemoSettings.asset");
            Assert.AreEqual(30f, demo.idleSeconds, 1e-4f);
            Assert.Greater(demo.shots.Length, 1); Assert.Greater(demo.dwellSeconds, 0f);
            var overlay = Resources.Load<OverlaySettings>(OverlaySettings.ResourcePath);
            Assert.IsNotNull(overlay, "Resources/OverlaySettings.asset");
            Assert.Greater(overlay.curriculum.Length, 0); Assert.Greater(overlay.benchmarkSeconds, 0f);
            var cam = Resources.Load<CameraSettings>(CameraSettings.ResourcePath);
            Assert.IsNotNull(cam, "Resources/CameraSettings.asset");
            Assert.AreEqual(18f, cam.orbitDegPerSecond, 1e-4f); Assert.AreEqual(0.5f, cam.cutAbove, 1e-4f);
            Assert.AreEqual(0.6f, cam.cutUnder, 1e-4f); Assert.AreEqual(3f, cam.cutSeconds, 1e-4f);
        }
    }
}
