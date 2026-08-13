"""txtai-xberg — Xberg document extraction pipeline."""

from txtai_xberg.pipeline import (
    DocumentMetadata,
    ExtractionDocument,
    ExtractionFailedError,
    IndexDocument,
    XbergPipeline,
)

__all__ = [
    "DocumentMetadata",
    "ExtractionDocument",
    "ExtractionFailedError",
    "IndexDocument",
    "XbergPipeline",
]
__version__ = "1.0.0rc32"
