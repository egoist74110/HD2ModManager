"""共用材质/贴图必须写进全局常驻 Archive（9ba626afa44a3aa3），不能跟着某件装备的 Archive。

回归：以前写进「首个域（通常是身体）」的 Archive 的 patch_2，只穿头盔时该 Archive 不加载，头盔材质缺失。
官方 IAS 包（Eku、Milfy SR-64、CE-07）都放在 9ba626afa44a3aa3。
"""
import inspect
import unittest

import package_model


class IasSharedMaterialArchiveTests(unittest.TestCase):
    def test_shared_resources_go_to_global_archive(self):
        self.assertEqual(package_model.SHARED_RESOURCE_ARCHIVE, "9ba626afa44a3aa3")
        source = inspect.getsource(package_model.pack)
        self.assertIn('f"{SHARED_RESOURCE_ARCHIVE}.patch_2"', source)
        self.assertNotIn("material_archive", source)


if __name__ == "__main__":
    unittest.main()
