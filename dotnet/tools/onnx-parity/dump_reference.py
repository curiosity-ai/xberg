#!/usr/bin/env python3
"""Dump per-node intermediate tensors from an ONNX model as a parity reference.

The C# ONNX runtime under ``Xberg.Internal.Onnx`` is a hand-written
re-implementation: it parses the same ``.onnx`` file and executes the same
graph with its own SIMD kernels. This script produces the ground truth that
implementation is checked against, one tensor per graph value, so a divergence
is attributed to the *first* node that produced it rather than showing up only
as a wrong final detection.

Usage::

    dump_reference.py MODEL.onnx OUT_DIR [--dim N=1] [--seed 7]
                      [--max-elements 4000000] [--input name=file.npy]

Every value in the graph is promoted to a graph output, so ORT's graph
optimisations are disabled and each node runs verbatim. That is the point:
we want the unfused reference, not the fastest path to the final output.

Outputs into ``OUT_DIR``:

``manifest.json``
    Nodes in topological order with op type, attributes, input/output names,
    plus the model inputs and the recorded shape/dtype of every tensor.
``tensors/<sanitised-name>.npy``
    One file per recorded value. Names are sanitised for the filesystem; the
    manifest maps original name to file.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
from onnx import numpy_helper

# ONNX TensorProto element type -> numpy dtype, for the types these models use.
ELEM_TYPE_TO_NUMPY = {
    onnx.TensorProto.FLOAT: np.float32,
    onnx.TensorProto.DOUBLE: np.float64,
    onnx.TensorProto.INT64: np.int64,
    onnx.TensorProto.INT32: np.int32,
    onnx.TensorProto.UINT8: np.uint8,
    onnx.TensorProto.INT8: np.int8,
    onnx.TensorProto.BOOL: np.bool_,
}


def sanitise(name: str) -> str:
    """A filesystem-safe stem for a graph value name.

    ONNX names carry ``/``, ``:`` and other separators freely, and two distinct
    names can sanitise to the same stem — so a short hash of the original is
    appended to keep the mapping injective.
    """
    stem = re.sub(r"[^A-Za-z0-9_.-]", "_", name)[:96]
    digest = hashlib.sha1(name.encode("utf-8")).hexdigest()[:8]
    return f"{stem}__{digest}"


def attribute_to_json(attr: onnx.AttributeProto) -> object:
    """Render an attribute as plain JSON so the manifest is diffable.

    Tensor-valued attributes are summarised rather than inlined: the C# side
    reads them straight out of the model file, so the manifest only needs
    enough to identify them.
    """
    t = attr.type
    A = onnx.AttributeProto
    if t == A.INT:
        return int(attr.i)
    if t == A.FLOAT:
        return float(attr.f)
    if t == A.STRING:
        return attr.s.decode("utf-8", "replace")
    if t == A.INTS:
        return [int(v) for v in attr.ints]
    if t == A.FLOATS:
        return [float(v) for v in attr.floats]
    if t == A.STRINGS:
        return [s.decode("utf-8", "replace") for s in attr.strings]
    if t == A.TENSOR:
        arr = numpy_helper.to_array(attr.t)
        return {"tensor": {"dtype": str(arr.dtype), "shape": list(arr.shape)}}
    return {"unsupported_attribute_type": int(t)}


def resolve_dims(value_info: onnx.ValueInfoProto, overrides: dict[str, int]) -> list[int]:
    """Concrete shape for a model input, resolving symbolic dims from --dim."""
    dims: list[int] = []
    for d in value_info.type.tensor_type.shape.dim:
        if d.HasField("dim_value") and d.dim_value > 0:
            dims.append(int(d.dim_value))
            continue
        key = d.dim_param or ""
        if key in overrides:
            dims.append(overrides[key])
        elif "N" in overrides:
            # The common case: a single unnamed batch dim, pinned with --dim N=1.
            dims.append(overrides["N"])
        else:
            raise SystemExit(
                f"input '{value_info.name}' has unresolved dim '{key or '?'}'; "
                f"pass --dim {key or 'N'}=<size>"
            )
    return dims


def synth_input(name: str, dtype: np.dtype, shape: list[int], rng: np.random.Generator) -> np.ndarray:
    """A deterministic stand-in for a real model input.

    Floats land in ``[0, 1)`` because that is the range the real preprocessing
    produces (``/255`` rescale, no ImageNet normalisation), so activations stay
    in the regime the weights were trained for and a kernel bug is not masked
    by everything saturating.
    """
    if np.issubdtype(dtype, np.floating):
        return rng.random(size=shape, dtype=np.float32).astype(dtype)
    if name == "orig_target_sizes" or "size" in name.lower():
        # RT-DETR reads this as [height, width] and scales boxes by it; feed the
        # real 640x640 so the box decode produces in-range coordinates.
        return np.tile(np.array([640, 640], dtype=dtype), (shape[0], 1)).reshape(shape)
    return rng.integers(0, 2, size=shape).astype(dtype)


def build_dump_model(model: onnx.ModelProto, max_elements: int) -> tuple[onnx.ModelProto, list[str]]:
    """Return a copy of the model with every intermediate value as an output.

    Values already declared as graph outputs are left alone. Initializers are
    skipped — the C# side reads those from the model file itself.
    """
    graph = model.graph
    existing = {o.name for o in graph.output}
    initializers = {i.name for i in graph.initializer}

    extra: list[str] = []
    for node in graph.node:
        for out in node.output:
            if out and out not in existing and out not in initializers:
                existing.add(out)
                extra.append(out)

    dump = onnx.ModelProto()
    dump.CopyFrom(model)
    for name in extra:
        # An empty ValueInfoProto lets ORT infer type and shape at run time,
        # which matters because several of these values are dynamically shaped.
        vi = dump.graph.output.add()
        vi.name = name
    return dump, extra


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("model", type=Path)
    ap.add_argument("out_dir", type=Path)
    ap.add_argument("--dim", action="append", default=[], metavar="NAME=SIZE",
                    help="pin a symbolic input dimension, e.g. --dim N=1")
    ap.add_argument("--input", action="append", default=[], metavar="NAME=FILE.npy",
                    help="use a real input tensor instead of synthesising one")
    ap.add_argument("--seed", type=int, default=7, help="RNG seed for synthesised inputs")
    ap.add_argument("--max-elements", type=int, default=4_000_000,
                    help="skip recording tensors larger than this (still executed)")
    args = ap.parse_args()

    overrides: dict[str, int] = {}
    for spec in args.dim:
        key, _, val = spec.partition("=")
        overrides[key] = int(val)
    overrides.setdefault("N", 1)

    supplied: dict[str, np.ndarray] = {}
    for spec in args.input:
        key, _, val = spec.partition("=")
        supplied[key] = np.load(val)

    model = onnx.load(str(args.model))
    graph = model.graph
    initializers = {i.name for i in graph.initializer}

    rng = np.random.default_rng(args.seed)
    feeds: dict[str, np.ndarray] = {}
    input_meta = []
    for vi in graph.input:
        if vi.name in initializers:
            continue  # Older exports list initializers as inputs too.
        if vi.name in supplied:
            arr = supplied[vi.name]
        else:
            elem = vi.type.tensor_type.elem_type
            dtype = ELEM_TYPE_TO_NUMPY.get(elem)
            if dtype is None:
                raise SystemExit(f"input '{vi.name}' has unsupported element type {elem}")
            arr = synth_input(vi.name, np.dtype(dtype), resolve_dims(vi, overrides), rng)
        feeds[vi.name] = arr
        input_meta.append({"name": vi.name, "dtype": str(arr.dtype), "shape": list(arr.shape)})

    dump_model, extra = build_dump_model(model, args.max_elements)
    print(f"promoting {len(extra)} intermediate values to graph outputs", file=sys.stderr)

    opts = ort.SessionOptions()
    # Fusion would erase exactly the per-node boundaries we are trying to observe.
    opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(dump_model.SerializeToString(), opts, providers=["CPUExecutionProvider"])

    out_names = [o.name for o in session.get_outputs()]
    print(f"running {args.model.name} with {len(out_names)} outputs", file=sys.stderr)
    results = session.run(out_names, feeds)

    tensor_dir = args.out_dir / "tensors"
    tensor_dir.mkdir(parents=True, exist_ok=True)

    recorded: dict[str, dict] = {}
    skipped: list[str] = []
    for name, arr in zip(out_names, results):
        arr = np.asarray(arr)
        if arr.size > args.max_elements:
            skipped.append(name)
            continue
        stem = sanitise(name)
        np.save(tensor_dir / f"{stem}.npy", arr)
        recorded[name] = {"file": f"tensors/{stem}.npy", "dtype": str(arr.dtype), "shape": list(arr.shape)}

    # Model inputs are recorded too, so the C# harness feeds byte-identical data.
    for name, arr in feeds.items():
        stem = sanitise(name)
        np.save(tensor_dir / f"{stem}.npy", arr)
        recorded[name] = {"file": f"tensors/{stem}.npy", "dtype": str(arr.dtype), "shape": list(arr.shape)}

    nodes = [
        {
            "index": i,
            "name": n.name,
            "op_type": n.op_type,
            "inputs": list(n.input),
            "outputs": list(n.output),
            "attributes": {a.name: attribute_to_json(a) for a in n.attribute},
        }
        for i, n in enumerate(graph.node)
    ]

    manifest = {
        "model": args.model.name,
        "model_sha256": hashlib.sha256(args.model.read_bytes()).hexdigest(),
        "opset": {(o.domain or "ai.onnx"): o.version for o in model.opset_import},
        "seed": args.seed,
        "dims": overrides,
        "graph_inputs": input_meta,
        "graph_outputs": [o.name for o in graph.output],
        "initializers": sorted(initializers),
        "nodes": nodes,
        "tensors": recorded,
        "skipped_too_large": skipped,
    }
    (args.out_dir / "manifest.json").write_text(json.dumps(manifest, indent=1))

    print(
        f"wrote {len(recorded)} tensors ({len(skipped)} skipped as too large) to {args.out_dir}",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
