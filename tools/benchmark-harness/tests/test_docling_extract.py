"""Conformance tests for the isolated Docling benchmark wrapper.

The batch path is driven by docling-jobkit's ``DoclingConverterManager``; the sync path still
uses docling's ``DocumentConverter``. These tests validate the wrapper's contract without
importing the real docling or docling-jobkit packages, so they run under a plain ``cargo test``.
"""

from __future__ import annotations

import importlib.util
import sys
import types
import unittest
from pathlib import Path
from typing import ClassVar
from unittest import mock


def _load_wrapper() -> types.ModuleType:
    docling = types.ModuleType("docling")
    converter_module = types.ModuleType("docling.document_converter")
    converter_module.DocumentConverter = object
    script = Path(__file__).parents[1] / "scripts" / "docling_extract.py"
    spec = importlib.util.spec_from_file_location("docling_extract_under_test", script)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    with mock.patch.dict(
        sys.modules,
        {"docling": docling, "docling.document_converter": converter_module},
    ):
        spec.loader.exec_module(module)
    return module


class _Status:
    def __init__(self, name: str) -> None:
        self.name = name


# The wrapper compares by identity against ConversionStatus.SUCCESS / PARTIAL_SUCCESS.
_SUCCESS = _Status("SUCCESS")
_PARTIAL_SUCCESS = _Status("PARTIAL_SUCCESS")
_FAILURE = _Status("FAILURE")


class _Document:
    def __init__(self, path: str, events: list[str]) -> None:
        self.path = path
        self.events = events

    def export_to_markdown(self) -> str:
        self.events.append(f"render:{self.path}")
        return f"markdown:{self.path}"

    def export_to_text(self) -> str:
        self.events.append(f"render_text:{self.path}")
        return f"text:{self.path}"


class _Result:
    def __init__(self, path: str, events: list[str], *, success: bool, partial: bool = False) -> None:
        if partial:
            self.status = _PARTIAL_SUCCESS
        else:
            self.status = _SUCCESS if success else _FAILURE
        self.document = _Document(path, events)
        # A partially converted document still carries content, but docling also reports the
        # recoverable problems it hit.
        self.errors = [f"page-warning:{path}"] if partial else ([] if success else [f"failed:{path}"])


class _Manager:
    """Stand-in for docling-jobkit's ``DoclingConverterManager``."""

    last: _Manager | None = None
    # Sources listed here yield PARTIAL_SUCCESS instead of the index-based default.
    partial_sources: ClassVar[set[str]] = set()

    def __init__(self, events: list[str], config: object) -> None:
        self.events = events
        self.config = config
        self.calls = 0
        self.sources: list[str] = []
        _Manager.last = self

    def convert_documents(self, *, sources, options):
        self.calls += 1
        self.sources = list(sources)
        self.options = options

        def stream():
            self.events.append("convert_documents")
            for index, path in enumerate(self.sources):
                self.events.append(f"yield:{path}")
                if path in _Manager.partial_sources:
                    yield _Result(path, self.events, success=False, partial=True)
                else:
                    yield _Result(path, self.events, success=index == 0)

        return stream()


def _jobkit_modules(events: list[str]) -> dict[str, types.ModuleType]:
    """Build the fake docling / docling-jobkit module tree the batch path imports lazily."""
    base_models = types.ModuleType("docling.datamodel.base_models")
    base_models.ConversionStatus = types.SimpleNamespace(SUCCESS=_SUCCESS, PARTIAL_SUCCESS=_PARTIAL_SUCCESS)
    base_models.OutputFormat = types.SimpleNamespace(MARKDOWN="md", TEXT="text")

    manager_mod = types.ModuleType("docling_jobkit.convert.manager")
    manager_mod.DoclingConverterManager = lambda config: _Manager(events, config)
    manager_mod.DoclingConverterManagerConfig = types.SimpleNamespace

    convert_mod = types.ModuleType("docling_jobkit.datamodel.convert")
    convert_mod.ConvertDocumentsOptions = types.SimpleNamespace

    return {
        "docling.datamodel": types.ModuleType("docling.datamodel"),
        "docling.datamodel.base_models": base_models,
        "docling_jobkit": types.ModuleType("docling_jobkit"),
        "docling_jobkit.convert": types.ModuleType("docling_jobkit.convert"),
        "docling_jobkit.convert.manager": manager_mod,
        "docling_jobkit.datamodel": types.ModuleType("docling_jobkit.datamodel"),
        "docling_jobkit.datamodel.convert": convert_mod,
    }


class DoclingBatchConformanceTest(unittest.TestCase):
    """Validate the wrapper contract without importing the real packages."""

    def test_converter_configuration_fails_closed(self) -> None:
        """Reject a sync run when the requested OCR mode cannot be configured."""
        wrapper = _load_wrapper()
        error: RuntimeError | None = None

        try:
            wrapper.create_converter(ocr_enabled=True)
        except RuntimeError as caught:
            error = caught
        else:
            self.fail("converter configuration unexpectedly succeeded")

        assert error is not None
        assert str(error) == "failed to configure Docling with OCR explicitly enabled"
        assert isinstance(error.__cause__, ImportError)

    def test_batch_uses_jobkit_single_lazy_ordered_timed(self) -> None:
        """The jobkit stream is consumed once, in order, inside the timer."""
        wrapper = _load_wrapper()
        events: list[str] = []

        def perf_counter() -> float:
            events.append("clock")
            return 10.0 if events.count("clock") == 1 else 12.0

        with (
            mock.patch.dict(sys.modules, _jobkit_modules(events)),
            mock.patch.object(wrapper.time, "perf_counter", side_effect=perf_counter),
            mock.patch.object(wrapper, "_get_peak_memory_bytes", return_value=123),
        ):
            payload = wrapper.extract_batch(["first.pdf", "second.pdf"], ocr_enabled=False)

        manager = _Manager.last
        assert manager is not None
        assert manager.calls == 1
        assert manager.sources == ["first.pdf", "second.pdf"]
        assert manager.options.do_ocr is False
        assert manager.options.abort_on_error is False
        # Timed around a single lazy pass: clock, then the batch call, ..., clock.
        assert events[0:2] == ["clock", "convert_documents"]
        assert events[-1] == "clock"
        # Lazy and ordered: each result is rendered before the next is pulled.
        assert events.index("yield:first.pdf") < events.index("render:first.pdf")
        assert events.index("render:first.pdf") < events.index("yield:second.pdf")
        # Output contract, unchanged from the previous batch engine.
        assert len(payload["results"]) == 2
        assert payload["results"][0]["content"] == "markdown:first.pdf"
        assert payload["results"][1]["error"] == "['failed:second.pdf']"
        assert payload["total_ms"] == 2000.0
        assert payload["per_file_ms"] == [None, None]
        assert payload["metadata"]["batch_api"] == "docling_jobkit.DoclingConverterManager.convert_documents"
        assert payload["metadata"]["benchmark_timing_scope"] == "cold_end_to_end_subprocess"

    def test_batch_plaintext_renders_export_to_text(self) -> None:
        """Plaintext output must render via ``export_to_text``, not markdown."""
        wrapper = _load_wrapper()
        events: list[str] = []

        with (
            mock.patch.dict(sys.modules, _jobkit_modules(events)),
            mock.patch.object(wrapper, "_get_peak_memory_bytes", return_value=1),
        ):
            payload = wrapper.extract_batch(["only.pdf"], ocr_enabled=False, output_format="plaintext")

        assert payload["results"][0]["content"] == "text:only.pdf"
        assert "render_text:only.pdf" in events

    def test_batch_renders_partial_success_content(self) -> None:
        """PARTIAL_SUCCESS documents must be rendered, not scored as empty.

        Docling reports PARTIAL_SUCCESS when a document converted but hit recoverable problems on
        some pages; the DoclingDocument still carries real content. Discarding it scored those
        documents as empty in batch mode while the single-file path rendered them normally, which
        understated docling. The degraded status is carried through as metadata instead.
        """
        wrapper = _load_wrapper()
        events: list[str] = []
        _Manager.partial_sources = {"partial.pdf"}
        try:
            with (
                mock.patch.dict(sys.modules, _jobkit_modules(events)),
                mock.patch.object(wrapper, "_get_peak_memory_bytes", return_value=7),
            ):
                payload = wrapper.extract_batch(["partial.pdf"], ocr_enabled=False)
        finally:
            _Manager.partial_sources = set()

        result = payload["results"][0]
        assert result["content"] == "markdown:partial.pdf"
        assert "error" not in result
        assert result["metadata"]["status"] == "PARTIAL_SUCCESS"
        assert result["metadata"]["partial_errors"] == "['page-warning:partial.pdf']"
        assert result["metadata"]["output_format"] == "markdown"


if __name__ == "__main__":
    unittest.main()
