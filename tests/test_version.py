"""版本号一致性回归：README / pyproject / CLIENT_ID / HostId 必须同步。"""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class TestVersionConsistency(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.readme = (ROOT / "README.md").read_text(encoding="utf-8")
        cls.pyproject = (ROOT / "python" / "pyproject.toml").read_text(encoding="utf-8")
        cls.protocol = (ROOT / "python" / "pywinui3" / "protocol.py").read_text(encoding="utf-8")
        cls.wire = (ROOT / "host" / "PyWinUI.Host" / "Protocol" / "Wire.cs").read_text(encoding="utf-8")
        cls.init = (ROOT / "python" / "pywinui3" / "__init__.py").read_text(encoding="utf-8")

    def test_pyproject_version(self) -> None:
        m = re.search(r'^version = "([^"]+)"', self.pyproject, re.M)
        self.assertIsNotNone(m)
        self.version = m.group(1)
        # 允许 PEP 440 的预发布后缀（rc1/rc2 等）
        self.assertTrue(re.fullmatch(r"\d+\.\d+\.\d+(?:rc\d+)?", self.version))

    def test_all_versions_match(self) -> None:
        m = re.search(r'^version = "([^"]+)"', self.pyproject, re.M)
        assert m is not None
        version = m.group(1)

        # README 顶部版本表 + 安装链接
        self.assertIn(f"| **版本 / 许可** | {version} ·", self.readme)
        self.assertIn(f"releases/download/v{version}/pywinui3-{version}-py3-none-win_amd64.whl", self.readme)

        # 协议标识
        self.assertIn(f'__version__ = "{version}"', self.init)
        self.assertIn(f'CLIENT_ID = "pywinui3/{version}"', self.protocol)
        self.assertIn(f'HostId = "PyWinUI.Host/{version}"', self.wire)

    def test_no_stale_version_in_readme(self) -> None:
        # README 里不应再出现与当前版本不同的 pywinui3-<ver> wheel 名
        found = set(re.findall(r"pywinui3-(\d+\.\d+\.\d+(?:rc\d+)?)-py3-none", self.readme))
        m = re.search(r'^version = "([^"]+)"', self.pyproject, re.M)
        assert m is not None
        self.assertEqual(found, {m.group(1)})


if __name__ == "__main__":
    unittest.main()
