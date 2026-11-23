"""Round rules and arena geometry, one source for training, the CPU scripts and Unity.

Unity reads the same numbers from joint_map.json "arena" (written by koth/build_mjcf.py):
  {"plateau_r", "slope_k", "bevel_w", "bevel_s", "out_radius", "fall_height", "fall_up"}

  ground      the concrete platform (2026-11-23): flat for r <= PLATEAU_R; a rounded rim BEVEL_W wide whose slope steepens
              from 0 to BEVEL_S, z = -(BEVEL_S / 2 BEVEL_W) d^2 with d = r - PLATEAU_R; then the hill, which carries on
              steepening, z = -BEVEL_S BEVEL_W / 2 - BEVEL_S e - SLOPE_K e^2 with e = d - BEVEL_W. The slope never
              eases off, so the profile stays concave and MuJoCo's convex hull of the arena mesh is the solid itself.
              BEVEL_W = BEVEL_S = 0 is the old dome, z = -SLOPE_K max(r - PLATEAU_R, 0)^2.
  out         radius > OUT_RADIUS, or pelvis up-axis z < FALL_UP, or pelvis height above the ground < FALL_HEIGHT
              (Unity MatchDirector, duel_stats.py, play.py).
  training    KothEnv ends an episode on tipped (up-axis z < TRAIN_TIP_UP, i.e. upside-down past horizontal),
              low (height < FALL_HEIGHT), radius > cfg["max_radius"] (OUT_RADIUS for the combat rungs) and, while
              cfg["terminate_on_leg_contact"], own foot-foot / foot-shin contact. The tipped threshold is looser
              than FALL_UP on purpose (a stumble that recovers is not a fall during training).
"""

import numpy as np

PLATEAU_R = 2.5  # a 5 m concrete platform (it was the 3 m summit of the dome until 2026-11-23)
BEVEL_W, BEVEL_S = 0.15, 0.5  # its rounded rim: 15 cm wide, ending at a 27 deg slope 3.75 cm down
SLOPE_K = 1.0 / 9.0
OUT_RADIUS = 2.8  # a little way down the slope: 0.15 m past the rim
FALL_HEIGHT = 0.3
FALL_UP = 0.3
TRAIN_TIP_UP = 0.0


def ground_z(r):
    """Ground height at distance r from the summit centre (scalar or numpy array)."""
    d = np.maximum(np.asarray(r, dtype=np.float64) - PLATEAU_R, 0.0)
    e = np.maximum(d - BEVEL_W, 0.0)
    rim = BEVEL_S / (2 * BEVEL_W) * np.minimum(d, BEVEL_W) ** 2 if BEVEL_W > 0 else 0.0 * d
    z = -rim - BEVEL_S * e - SLOPE_K * e**2
    return float(z) if np.ndim(r) == 0 else z


def ground_slope(r):
    """|dz/dr| of the ground at distance r (scalar or numpy array)."""
    d = np.maximum(np.asarray(r, dtype=np.float64) - PLATEAU_R, 0.0)
    e = np.maximum(d - BEVEL_W, 0.0)
    rim = BEVEL_S / BEVEL_W * np.minimum(d, BEVEL_W) if BEVEL_W > 0 else 0.0 * d
    s = np.where(e > 0, BEVEL_S + 2 * SLOPE_K * e, rim)
    return float(s) if np.ndim(r) == 0 else s


def upright(quat) -> float:
    """World z of the body z axis for a wxyz quaternion (1 = upright)."""
    return 1 - 2 * (quat[1] ** 2 + quat[2] ** 2)


def is_out(pos, quat, arena: bool = True) -> bool:
    """Unity's round rule for one fighter. pos = pelvis xyz, quat = pelvis wxyz (numpy / sequences).
    arena=False (flat floor): only the fall tests apply."""
    r = np.hypot(pos[0], pos[1])
    h = ground_z(r) if arena else 0.0
    return bool((arena and r > OUT_RADIUS) or upright(quat) < FALL_UP or pos[2] - h < FALL_HEIGHT)


def arena_json() -> dict:
    """The joint_map.json "arena" object (Unity mirror)."""
    return {
        "plateau_r": PLATEAU_R,
        "slope_k": SLOPE_K,
        "bevel_w": BEVEL_W,
        "bevel_s": BEVEL_S,
        "out_radius": OUT_RADIUS,
        "fall_height": FALL_HEIGHT,
        "fall_up": FALL_UP,
    }
