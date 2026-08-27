#!/usr/bin/env python3
"""Score an OCR-extracted document against a plain-text ground truth, line by line.

Why this exists as a committed script rather than an ad-hoc command: a previous measurement
recorded "GT 243/267" for ordinance_2197, but the ground-truth file has 313 non-empty lines, so
the scoring definition could not be reconstructed from the numbers alone and the result became
uncomparable. The definition below is the contract; change it only deliberately, and re-baseline
every control when you do.

Two numbers are reported per document:

  recovered  -- ground-truth lines found in the output. This is recall against the transcription.
  unmatched  -- output lines that correspond to no ground-truth line. This is the reproducible
                proxy for "junk". It is deliberately a SUPERSET of junk: legitimate content the
                ground truth omits also lands here.

`unmatched` is a pointer, not a verdict. Always read the list (--show-unmatched) rather than
trusting the count -- four separate OCR changes in this repo were approved on a metric and then
rejected on reading the output.

Matching is substring-based on a normalised stream rather than line-to-line equality, because
extraction re-wraps lines: a ground-truth line split across two output lines must still count as
recovered, or the metric would punish correct extraction for its line breaks.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

# A line needs some minimum substance before it is worth scoring. Page numbers ("5"), rule
# fragments and stray punctuation are not evidence either way, and counting them swamps both
# metrics with noise that no change can move.
MIN_SCORABLE_CHARS = 4

# Markdown scaffolding the extractor adds that the plain-text ground truth never contains.
# Stripping it prevents every table separator and page marker from being reported as junk.
_SCAFFOLDING = re.compile(
    r"""^\s*(
        \|?\s*[-:]{3,}[-:\s|]*\|?      # table separator rows
      | <!--.*-->                       # page markers and other comments
      | [-*_]{3,}                       # horizontal rules
      | ```.*                           # fence delimiters
    )\s*$""",
    re.VERBOSE,
)

_MARKUP = re.compile(r"[#*_`>|]+")
_LIST_MARKER = re.compile(r"^\s*(?:[-*+]|\d+[.)])\s+")
_WS = re.compile(r"\s+")


def emit(message: str = "") -> None:
    """Write one report line to stdout.

    Routed through a single function so this reporting tool needs exactly one `print`
    suppression rather than one per call site. `poly.toml` already exempts comparable
    tools (`tools/perf/*.py`), but it is alef-generated and carries a content hash, so
    adding a path there by hand would be reverted by the next regen and flag `alef verify`.
    """
    print(message)  # noqa: T201 -- stdout IS this tool's output contract


def normalise(text: str) -> str:
    """Collapse a line to its comparable core: no markup, no case, single spaces."""
    text = _LIST_MARKER.sub("", text)
    text = _MARKUP.sub(" ", text)
    text = _WS.sub(" ", text)
    return text.strip().casefold()


def scorable_lines(raw: str) -> list[tuple[str, str]]:
    """Return (original, normalised) for lines carrying enough substance to score."""
    out: list[tuple[str, str]] = []
    for line in raw.splitlines():
        if _SCAFFOLDING.match(line):
            continue
        norm = normalise(line)
        if len(norm) < MIN_SCORABLE_CHARS:
            continue
        if not any(character.isalpha() for character in norm):
            continue
        out.append((line.rstrip(), norm))
    return out


def score(output_text: str, ground_truth_text: str) -> dict:
    """Compare an extracted document against a ground truth and return the two headline counts.

    Returns `recovered` (ground-truth recall) and `unmatched` (the junk proxy), each with the
    corresponding line list so the caller can READ them rather than trust the number.
    """
    gt = scorable_lines(ground_truth_text)
    out = scorable_lines(output_text)

    # Compare against one normalised stream so a ground-truth line that the extractor re-wrapped
    # across several output lines still matches.
    output_stream = " ".join(norm for _, norm in out)
    gt_stream = " ".join(norm for _, norm in gt)

    recovered = [original for original, norm in gt if norm in output_stream]
    missing = [original for original, norm in gt if norm not in output_stream]
    unmatched = [original for original, norm in out if norm not in gt_stream]

    return {
        "gt_total": len(gt),
        "recovered": len(recovered),
        "missing": len(missing),
        "output_total": len(out),
        "unmatched": len(unmatched),
        "missing_lines": missing,
        "unmatched_lines": unmatched,
    }


def main() -> int:
    """Score one document and apply any pre-registered literal falsifiers.

    Exits 1 when an `--expect` literal is absent or a `--forbid` literal is present, so a
    falsifier stated before the run fails the command rather than needing a human to notice.
    """
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("output", type=Path, help="extracted document (markdown or text)")
    parser.add_argument("ground_truth", type=Path, help="plain-text ground truth")
    parser.add_argument("--json", action="store_true", help="emit the full result as JSON")
    parser.add_argument("--show-unmatched", action="store_true", help="print every unmatched output line")
    parser.add_argument("--show-missing", action="store_true", help="print every unrecovered ground-truth line")
    parser.add_argument(
        "--expect",
        metavar="LITERAL",
        action="append",
        default=[],
        help="assert this literal IS present in the output; repeatable. Exits 1 if absent.",
    )
    parser.add_argument(
        "--forbid",
        metavar="LITERAL",
        action="append",
        default=[],
        help="assert this literal is NOT present in the output; repeatable. Exits 1 if present.",
    )
    args = parser.parse_args()

    for path in (args.output, args.ground_truth):
        if not path.is_file():
            sys.stderr.write(f"not a file: {path}\n")
            return 2

    output_text = args.output.read_text(encoding="utf-8", errors="replace")
    result = score(output_text, args.ground_truth.read_text(encoding="utf-8", errors="replace"))

    # Literal assertions run against the RAW output, not the normalised stream: a falsifier is
    # stated in the exact characters a reader would look for.
    present = [literal for literal in args.expect if literal not in output_text]
    forbidden = [literal for literal in args.forbid if literal in output_text]

    if args.json:
        emit(json.dumps({**result, "expect_absent": present, "forbid_present": forbidden}, indent=2))
    else:
        emit(
            f"{args.output.name}: recovered {result['recovered']}/{result['gt_total']} GT lines, "
            f"{result['unmatched']} unmatched of {result['output_total']} output lines"
        )
        if args.show_missing:
            emit("\n-- ground-truth lines NOT recovered --")
            for line in result["missing_lines"]:
                emit(f"  {line}")
        if args.show_unmatched:
            emit("\n-- output lines matching no ground truth (READ THESE) --")
            for line in result["unmatched_lines"]:
                emit(f"  {line}")
        for literal in present:
            emit(f"EXPECTED BUT ABSENT: {literal!r}")
        for literal in forbidden:
            emit(f"FORBIDDEN BUT PRESENT: {literal!r}")

    return 1 if present or forbidden else 0


if __name__ == "__main__":
    sys.exit(main())
