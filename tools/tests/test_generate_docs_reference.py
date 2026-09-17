from __future__ import annotations

import importlib.util
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
GENERATOR_PATH = REPO_ROOT / "tools" / "generate_docs_reference.py"


def _load_generator():
    spec = importlib.util.spec_from_file_location(
        "generate_docs_reference", GENERATOR_PATH
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def test_bare_collection_types_render_without_version_specific_any() -> None:
    generator = _load_generator()

    assert generator._render_type(list) == "list"
    assert generator._render_type(dict) == "dict"
    assert generator._render_type(tuple) == "tuple"
