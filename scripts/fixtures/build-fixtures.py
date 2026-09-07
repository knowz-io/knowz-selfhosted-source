"""
Build binary smoke-test fixtures (PDF + PNG).
Run: python selfhosted/scripts/fixtures/build-fixtures.py

Regenerates test-seed.pdf (1-page minimal PDF, ~100 words of text) and
test-seed.png (1024x1024 PNG with "seed image diagram" text overlay)
in-place. Commit the resulting binaries; rerun only when source text
changes (avoids shipping ImageMagick/pandoc as CI prerequisites).
"""
from __future__ import annotations

import zlib
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent

PDF_TEXT = (
    "This is a seed pdf document used by the Knowz self-hosted enterprise "
    "post-deploy smoke test. It exercises Document Intelligence extraction "
    "end to end, producing a brief summary, content tags, and at least one "
    "semantic chunk that the Azure AI Search index can return on a keyword "
    "query. The phrase seed pdf document appears verbatim so the search "
    "assertion has a stable anchor across enrichment runs. Do not delete."
)

PNG_TEXT = "seed image diagram"


def build_pdf(dest: Path) -> None:
    """Emit a valid minimal 1-page PDF 1.4 file with PDF_TEXT as body."""

    def esc(s: str) -> str:
        return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")

    content_stream = (
        "BT\n"
        "/F1 12 Tf\n"
        "72 720 Td\n"
        "14 TL\n"
    )
    # Word-wrap at ~70 chars to keep within page width.
    words = PDF_TEXT.split()
    line, lines = "", []
    for w in words:
        if len(line) + len(w) + 1 > 70:
            lines.append(line)
            line = w
        else:
            line = f"{line} {w}".strip()
    if line:
        lines.append(line)
    for i, ln in enumerate(lines):
        if i == 0:
            content_stream += f"({esc(ln)}) Tj\n"
        else:
            content_stream += f"T* ({esc(ln)}) Tj\n"
    content_stream += "ET\n"
    content_bytes = content_stream.encode("latin-1")

    objects: list[bytes] = []
    objects.append(b"<< /Type /Catalog /Pages 2 0 R >>")
    objects.append(b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    objects.append(
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"
    )
    objects.append(
        b"<< /Length "
        + str(len(content_bytes)).encode("ascii")
        + b" >>\nstream\n"
        + content_bytes
        + b"\nendstream"
    )
    objects.append(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")

    buf = bytearray()
    buf += b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n"
    offsets = [0]
    for i, obj in enumerate(objects, start=1):
        offsets.append(len(buf))
        buf += f"{i} 0 obj\n".encode("ascii") + obj + b"\nendobj\n"
    xref_offset = len(buf)
    buf += f"xref\n0 {len(objects) + 1}\n".encode("ascii")
    buf += b"0000000000 65535 f \n"
    for off in offsets[1:]:
        buf += f"{off:010d} 00000 n \n".encode("ascii")
    buf += (
        f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\n"
        f"startxref\n{xref_offset}\n%%EOF\n"
    ).encode("ascii")

    dest.write_bytes(bytes(buf))


def build_png(dest: Path) -> None:
    """Emit a 1024x1024 PNG with PNG_TEXT rendered in large readable text."""
    size = 1024
    img = Image.new("RGB", (size, size), color=(245, 245, 250))
    draw = ImageDraw.Draw(img)

    font = None
    for candidate in (
        "arial.ttf",
        "C:/Windows/Fonts/arial.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "DejaVuSans-Bold.ttf",
    ):
        try:
            font = ImageFont.truetype(candidate, 96)
            break
        except (OSError, IOError):
            continue
    if font is None:
        font = ImageFont.load_default()

    bbox = draw.textbbox((0, 0), PNG_TEXT, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    x = (size - tw) // 2 - bbox[0]
    y = (size - th) // 2 - bbox[1]
    # Border so Vision has high-contrast edges to anchor OCR on.
    draw.rectangle((32, 32, size - 32, size - 32), outline=(30, 30, 30), width=4)
    draw.text((x, y), PNG_TEXT, fill=(20, 20, 20), font=font)
    draw.text(
        (size // 2 - 220, size - 120),
        "knowz post-deploy smoke",
        fill=(90, 90, 90),
        font=ImageFont.load_default(),
    )

    img.save(dest, format="PNG", optimize=True)


if __name__ == "__main__":
    pdf_path = HERE / "test-seed.pdf"
    png_path = HERE / "test-seed.png"
    build_pdf(pdf_path)
    build_png(png_path)
    # Sanity: PDF zlib hint used only to verify module; keeps import consumed.
    _ = zlib.adler32(PDF_TEXT.encode())
    print(f"wrote {pdf_path} ({pdf_path.stat().st_size} bytes)")
    print(f"wrote {png_path} ({png_path.stat().st_size} bytes)")
