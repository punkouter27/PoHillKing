using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// The motion-quality penalty weights of the training reward, one per term, editable in the Inspector
    /// (Data/Resources/RewardWeights.asset). They mirror Training/config/reward_penalties.json, which the Python trainer
    /// reads (koth/env.py reward_penalties); RewardWeightsTests keeps the two equal. The game uses them only for the
    /// RewardMeter readout in the Tab overlay: the shipped brains are ONNX files, so a changed weight affects the next
    /// training run, never the brains already in RL_Models.
    /// Each term is weight * value * control dt (0.02 s) per policy step, as in env.py.
    /// </summary>
    [CreateAssetMenu(menuName = "PoHillKing/Reward Weights", fileName = "RewardWeights")]
    public class RewardWeights : ScriptableObject
    {
        [Tooltip("ang_vel_xy: pelvis roll/pitch angular velocity squared (rad/s)^2")] public float angVelXy = -0.05f;
        [Tooltip("action_rate: action jitter, |a_t - a_t-1|^2 over the 29 joints")] public float actionRate = -0.01f;
        [Tooltip("feet_slip: planar speed (m/s) of each foot while it touches the ground")] public float feetSlip = -0.1f;
        [Tooltip("joint_vel: joint angular velocity squared, summed over the 29 joints")] public float jointVel = -0.0002f;
        [Tooltip("joint_torque: actuator torque squared (N m)^2, summed over the 29 actuators")] public float jointTorque = -1e-5f;

        public const string ResourcePath = "RewardWeights";
        static RewardWeights _current;
        /// <summary>The project's asset, or the defaults above when it is missing.</summary>
        public static RewardWeights Current
        {
            get
            {
                if (_current == null) _current = Resources.Load<RewardWeights>(ResourcePath);
                if (_current == null) _current = CreateInstance<RewardWeights>();
                return _current;
            }
        }
    }
}
