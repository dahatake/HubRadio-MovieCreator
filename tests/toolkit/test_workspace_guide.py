"""FR-135 AC-149: users-guide の Workspace 構造の文書が README から辿れ、実際のコードの構造と一致する。"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOC = ROOT / "users-guide" / "app" / "05-workspace-structure.md"
SRC = ROOT / "src"


def _doc() -> str:
    return DOC.read_text(encoding="utf-8")


def test_fr135_ac149_readme_links_to_doc():
    assert "app/05-workspace-structure.md" in (ROOT / "users-guide" / "README.md").read_text(encoding="utf-8")


def test_fr135_ac149_required_sections():
    text = _doc()
    for word in ("使えない名前", "移行", "書き出し", "Project 名とフォルダー名", "画面とフォルダーの対応"):
        assert word in text, word


def test_fr135_ac149_folder_names_match_code():
    text = _doc()
    store = (SRC / "HubRadio.Infrastructure/Files/ArtifactStore.cs").read_text(encoding="utf-8")
    kinds = re.search(r"GetKindFolder\(ArtifactKind kind\) =>(.*?)};", store, re.S).group(1)
    for folder in re.findall(r'=> "([a-z]+)"', kinds):
        assert f"{folder}\\" in text, folder
    folders = (SRC / "HubRadio.Infrastructure/Files/ProjectFolders.cs").read_text(encoding="utf-8")
    assert re.search(r'RegistryFileName = "([^"]+)"', folders).group(1) in text
    assert f"{re.search(r'MaxNameLength = (\d+)', folders).group(1)} 文字を超える" in text
    assert 'ProjectsDirectoryName = "projects"' in folders and "projects\\" in text
    loc = (SRC / "HubRadio.Infrastructure/Settings/WorkspaceLocationStore.cs").read_text(encoding="utf-8")
    assert re.search(r'ConfigFileName = "([^"]+)"', loc).group(1) in text
    assert re.search(r'DatabaseFileName = "([^"]+)"', loc).group(1) in text
    for reserved in ("CON", "NUL", "COM1", "LPT9"):
        assert reserved in text
