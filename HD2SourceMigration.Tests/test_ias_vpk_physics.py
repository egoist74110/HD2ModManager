"""使用 Generator 和 AQ SDK 的模块路径运行 VPK 物理编译回归。"""
import math
import unittest

from ias_physics.format_v1 import build, parse
from package_model import build_domain_chains


class IasVpkPhysicsTests(unittest.TestCase):
    def test_helmet_preserves_source_damping_per_second_at_solver_step(self):
        row = {"bone": "hair", "tipBone": "tip", "sourceStiffness": 100,
               "sourceDamping": 10, "sourceAngularDamping": 7, "angleLimitRad": 0.8}
        for step in (1 / 60, 1 / 120):
            with self.subTest(step=step):
                options = {} if step == 1 / 60 else {"fixed_dt": step}
                chains = build_domain_chains([row], {"hair"}, "helmet", **options)
                bones = [
                    {"name": "head", "parent_slot": -1, "palette_slot": 0, "rest_head": [0, 0, 0]},
                    {"name": "hair", "parent_slot": 0, "palette_slot": 1, "rest_head": [0, 0, 0.1]},
                    {"name": "tip", "parent_slot": 1, "palette_slot": 2, "rest_head": [0, 0, 0.2]},
                ]
                blob, _ = build({"unit_id": "0000000000000001", "palette_slots": 3,
                                 "fixed_dt": step, "bones": bones, "chains": chains})
                compiled = parse(blob)
                self.assertEqual([j["bone"] for j in compiled["joints"]], ["hair", "tip"])
                damping = compiled["chains"][0]["damping"]
                self.assertAlmostEqual(-math.log1p(-damping) / compiled["fixed_dt"], 7, places=5)

    def test_helmet_zero_damping_and_body_existing_mapping(self):
        row = {"bone": "hair", "tipBone": "tip", "sourceStiffness": 100,
               "sourceDamping": 0, "sourceAngularDamping": 0}
        self.assertEqual(build_domain_chains([row], {"hair"}, "helmet")[0]["damping"], 0)
        self.assertEqual(build_domain_chains([row], {"hair"}, "body")[0]["damping"], 0.15)
        row["sourceAngularDamping"] = None
        self.assertNotIn("damping", build_domain_chains([row], {"hair"}, "helmet")[0])
