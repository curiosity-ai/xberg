#!/usr/bin/env python3
"""Build an RT-DETR input tensor from a page image, or from a synthetic stand-in.

Two jobs. First, it encodes the preprocessing contract the Rust runtime uses
(``layout::preprocessing::preprocess_rescale``): bilinear resize to an exact
640x640 square — aspect ratio is *not* preserved, and the model compensates via
``orig_target_sizes`` — then a plain ``/255`` rescale into NCHW float32, with no
ImageNet mean/std normalisation. Getting any of that wrong shifts every box.

Second, with ``--synthetic`` it renders a document-shaped page (title, columns
of text lines, a table grid, a figure block). Random noise makes every detection
score near-identical, so rank order flips on float-level differences and tells
you nothing; a page with real structure produces well-separated scores, which is
what makes an end-to-end comparison meaningful.

Usage::

    make_page_input.py OUT_DIR [--image page.png] [--synthetic] [--size 640]
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

INPUT_SIZE = 640


def synthetic_page(width: int = 1240, height: int = 1754) -> Image.Image:
    """An A4-proportioned page with the block structure a layout model expects."""
    page = Image.new("RGB", (width, height), (255, 255, 255))
    draw = ImageDraw.Draw(page)
    margin = int(width * 0.08)
    right = width - margin

    def text_block(top: int, left: int, block_right: int, lines: int, leading: int, weight: int, ragged=True):
        """A run of filled bars standing in for lines of set text."""
        y = top
        for i in range(lines):
            end = block_right
            if ragged and i == lines - 1:
                end = left + int((block_right - left) * 0.62)
            draw.rectangle([left, y, end, y + weight], fill=(20, 20, 20))
            y += leading
        return y

    # Title, then a subtitle, then two columns of body text.
    draw.rectangle([margin, int(height * 0.06), int(width * 0.72), int(height * 0.06) + 34], fill=(0, 0, 0))
    y = text_block(int(height * 0.11), margin, right, 2, 26, 12)

    gutter = int(width * 0.04)
    column_width = (right - margin - gutter) // 2
    left_column = margin
    right_column = margin + column_width + gutter
    y_after = text_block(y + 30, left_column, left_column + column_width, 18, 22, 8)
    text_block(y + 30, right_column, right_column + column_width, 18, 22, 8)

    # A ruled table.
    table_top = y_after + 60
    table_bottom = table_top + 260
    rows, columns = 7, 4
    for r in range(rows + 1):
        ty = table_top + r * (table_bottom - table_top) // rows
        draw.line([margin, ty, right, ty], fill=(0, 0, 0), width=2)
    for c in range(columns + 1):
        tx = margin + c * (right - margin) // columns
        draw.line([tx, table_top, tx, table_bottom], fill=(0, 0, 0), width=2)
    for r in range(rows):
        for c in range(columns):
            cx = margin + c * (right - margin) // columns + 14
            cy = table_top + r * (table_bottom - table_top) // rows + 14
            draw.rectangle([cx, cy, cx + 90, cy + 9], fill=(40, 40, 40))

    # A figure with a caption beneath it.
    figure_top = table_bottom + 70
    draw.rectangle([margin, figure_top, margin + int((right - margin) * 0.55), figure_top + 300],
                   fill=(210, 210, 210), outline=(0, 0, 0))
    for i in range(6):
        bar_height = 40 + i * 34
        bx = margin + 40 + i * 60
        draw.rectangle([bx, figure_top + 270 - bar_height, bx + 40, figure_top + 270], fill=(90, 90, 90))
    text_block(figure_top + 320, margin, margin + int((right - margin) * 0.55), 2, 20, 7)

    # Page footer.
    draw.rectangle([margin, height - int(height * 0.05), margin + 180, height - int(height * 0.05) + 8],
                   fill=(60, 60, 60))
    return page


def preprocess(image: Image.Image, size: int) -> np.ndarray:
    """Resize to size x size bilinearly, rescale to [0,1], and emit NCHW float32."""
    resized = image.convert("RGB").resize((size, size), Image.BILINEAR)
    pixels = np.asarray(resized, dtype=np.float32) / 255.0  # HWC
    return np.transpose(pixels, (2, 0, 1))[None, ...].copy()  # NCHW


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out_dir", type=Path)
    ap.add_argument("--image", type=Path, help="page image to preprocess")
    ap.add_argument("--synthetic", action="store_true", help="render a synthetic document page")
    ap.add_argument("--size", type=int, default=INPUT_SIZE)
    args = ap.parse_args()

    if args.image is not None:
        page = Image.open(args.image)
    elif args.synthetic:
        page = synthetic_page()
    else:
        raise SystemExit("pass --image or --synthetic")

    args.out_dir.mkdir(parents=True, exist_ok=True)
    tensor = preprocess(page, args.size)
    np.save(args.out_dir / "images.npy", tensor)
    # orig_target_sizes is [height, width] of the *source* page: RT-DETR scales its
    # normalised boxes by this, so it must be the pre-resize geometry, not 640x640.
    np.save(args.out_dir / "orig_target_sizes.npy",
            np.array([[page.height, page.width]], dtype=np.int64))
    page.save(args.out_dir / "page.png")

    print(f"page {page.width}x{page.height} -> tensor {tensor.shape} in {args.out_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
