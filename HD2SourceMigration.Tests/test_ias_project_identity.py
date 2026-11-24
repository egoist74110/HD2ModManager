"""Milfy SDK 导出样本的 IAS 1.6.4 项目身份和点网格测试向量。"""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "HD2SourceMigration/IasPackaging/Scripts"))
from project_identity import owner_id, point_id, project_filename


class IasProjectIdentityTests(unittest.TestCase):
    def test_milfy_body_project_filename_matches_owner(self):
        project = "30d54711a4a94d60bb484df55f52f43b"
        archive = "b186eae3f2b61509"
        name = project_filename(project, archive, "hd2preview")
        self.assertEqual(name, "30d54711a4a94d60bb484df55f52f43b.b186eae3f2b61509.hd2preview")
        file_project, file_archive, _ = name.rsplit(".", 2)
        self.assertEqual(owner_id(file_project, file_archive), "owned_b0804930b32ba740bebfa18f")

    def test_milfy_helmet_project_filename_matches_owner(self):
        project = "417bd0e901c2489593ec9642ccd47ea9"
        archive = "84fd7aab7cf6f62d"
        self.assertEqual(project_filename(project, archive, "rigbin"),
                         "417bd0e901c2489593ec9642ccd47ea9.84fd7aab7cf6f62d.rigbin")
        self.assertEqual(owner_id(project, archive), "owned_c9fed135c6894c618465c253")

    def test_milfy_body_point_ids_match_isolation_routes(self):
        project = "30d54711a4a94d60bb484df55f52f43b"
        archive = "b186eae3f2b61509"
        for source, published in [("0c74e05811da7b90", "b45c16a6c67aad62"),
                                  ("feb7708e2fb358ed", "0803c9bbc6babaf6")]:
            with self.subTest(source=source):
                self.assertEqual(f"{point_id(project, archive, int(source, 16)):016x}", published)
