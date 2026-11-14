"""Profile of the 4th fighter "Brawler" (a 1.84 m, 118 kg heavyweight wrestler generated procedurally by
tools/make_brawler.py): the numbers koth/build_human.py turns into assets/brawler/brawler_mjx.xml.
Then `KOTH_ROBOT=brawler python -m koth.build_mjcf` builds his training scenes.

Kept off Game.unity and scene_koth_game.xml until R0 -> R1 -> getup -> hill brains are trained and graduated.
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROBOT = "brawler"
ASSETS = os.path.normpath(os.path.join(HERE, "..", "assets", "brawler"))

MASS_KG = 118.0
SPAWN_Z = 0.98
LOCAL_X = True
FRACTION = dict(head=0.0694, chest=0.1696, abdomen=0.1533, pelvis=0.1117, upper_arm=0.0291, forearm=0.0172, hand=0.0061,
                thigh=0.1416, shank=0.0423, foot=0.0127)
CAPS = dict(pelvis=0.16, abdomen=0.16, chest=0.155, head=0.11, thigh=0.095, upper_arm=0.062, forearm=0.048, hand=0.036)

DEFAULT_POSE = {
    "left_hip_pitch_joint": -0.14, "left_knee_joint": 0.28, "left_ankle_pitch_joint": -0.14,
    "right_hip_pitch_joint": -0.14, "right_knee_joint": 0.28, "right_ankle_pitch_joint": -0.14,
    "left_elbow_joint": 0.35, "right_elbow_joint": 0.35,
}

PD_GAINS = {"brawler": (0, 0), "brawler_hip": (660, 16), "brawler_knee": (880, 16), "brawler_ankle": (180, 5.5), "brawler_ankle_pitch": (780, 12),
            "brawler_waist_yaw": (560, 12), "brawler_waist_pitch": (760, 16), "brawler_waist_roll": (760, 16),
            "brawler_shoulder": (160, 5.0), "brawler_elbow": (115, 3.5), "brawler_wrist": (26, 0.8)}

JOINT_CLASSES = {
    "brawler_hip": {"brawler_hip_pitch": ("0 1 0", (-1.9, 0.45), 210, 0.03), "brawler_hip_roll": ("1 0 0", None, 145, 0.03), "brawler_hip_yaw": ("0 0 1", (-0.65, 0.65), 75, 0.016)},
    "brawler_knee": ("0 1 0", (-0.05, 2.3), 210, 0.03),
    "brawler_ankle": {"brawler_ankle_pitch": ("0 1 0", (-0.45, 0.75), 150, 0.016), "brawler_ankle_roll": ("1 0 0", (-0.36, 0.36), 45, 0.008)},
    "brawler_waist_yaw": ("0 0 1", (-0.7, 0.7), 88, 0.03), "brawler_waist_roll": ("1 0 0", (-0.42, 0.42), 145, 0.03), "brawler_waist_pitch": ("0 1 0", (-0.3, 0.9), 205, 0.03),
    "brawler_shoulder": {"brawler_shoulder_pitch": ("0 1 0", (-2.6, 0.9), 82, 0.0065), "brawler_shoulder_roll": ("1 0 0", None, 78, 0.0065), "brawler_shoulder_yaw": ("0 0 1", (-1.4, 1.4), 46, 0.004)},
    "brawler_elbow": ("0 -1 0", (0.0, 2.4), 64, 0.005),
    "brawler_wrist": {"brawler_wrist_roll": ("0 0 1", (-1.3, 1.3), 13, 0.0012), "brawler_wrist_pitch": ("0 1 0", (-0.95, 0.95), 11, 0.0012), "brawler_wrist_yaw": ("1 0 0", (-0.35, 0.35), 11, 0.0012)},
}

SIDE_RANGE = {"hip_roll": (-0.35, 0.8), "shoulder_roll": (-0.25, 2.5)}
DUMMY = dict(hip=0.8, waist=1.0, shoulder=0.4, wrist=0.09, ankle=0.3)


def build() -> str:
    from koth import build_human
    return build_human.build(sys.modules[__name__] if "sys" in globals() else __import__("sys").modules[__name__])


if __name__ == "__main__":
    build()
