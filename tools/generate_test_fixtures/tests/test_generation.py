"""Smoke test: every generator runs end-to-end into a tmp dir.

Asserts that the generator produces non-empty binary fixtures and that
every ``*.gt.json`` sidecar parses to a dict with the expected keys.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from generate_test_fixtures import (
    diff_pairs,
    docx_revisions,
    odt_revisions,
    pdf_incremental,
    pptx_comments,
    security_fixtures,
    xlsx_revisions,
)

GENERATORS = [
    docx_revisions,
    odt_revisions,
    xlsx_revisions,
    pptx_comments,
    pdf_incremental,
    diff_pairs,
    security_fixtures,
]


@pytest.fixture()
def repo_root(tmp_path: Path) -> Path:
    """A fake repo root with a ``test_documents/`` marker so relative-path
    resolution in the ground-truth writer succeeds.
    """
    (tmp_path / "Cargo.toml").write_text("# stub for fixture tests\n", encoding="utf-8")
    (tmp_path / "test_documents").mkdir()
    return tmp_path


@pytest.mark.parametrize("module", GENERATORS, ids=lambda m: m.__name__.rsplit(".", 1)[-1])
def test_generator_runs_and_emits_well_formed_outputs(module, tmp_path: Path, repo_root: Path) -> None:
    """Each generator runs without raising and every sidecar parses cleanly."""
    output_root = tmp_path / "out"
    output_root.mkdir()

    written = module.generate(output_root, repo_root)

    assert isinstance(written, list)
    for path in written:
        assert path.exists(), f"{module.__name__} reported {path} but it does not exist"
        assert path.stat().st_size > 0, f"{path} is zero-length"
        if path.suffix == ".json":
            payload = json.loads(path.read_text(encoding="utf-8"))
            assert isinstance(payload, dict), f"{path} is not a JSON object"
            for key in ("fixture_path", "format", "feature", "expectations", "generated_by"):
                assert key in payload, f"{path} missing {key!r}"
