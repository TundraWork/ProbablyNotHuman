"""Draw workshop/preview.png: 128x128 pixel art, written as a 1024x1024 PNG. Standard library only.

    python3 workshop/make_preview.py

Flat colours only: a CRT with a kaomoji on its screen, and the title underneath.
"""
from __future__ import annotations
import struct
import zlib
from pathlib import Path

SIZE, SCALE = 128, 8

BACKGROUND = "#191a2c"
INK = "#0b0c16"        # outline
CASE = "#ebe4d2"       # monitor casing, title
DETAIL = "#b9b09a"     # vents
SCREEN = "#0e1c1b"
PHOSPHOR = "#63f5a6"   # the kaomoji, the power light, "NOT"

FONT = {  # 5 rows; widths vary
    "P": ["1110", "1001", "1110", "1000", "1000"], "R": ["1110", "1001", "1110", "1010", "1001"],
    "O": ["0110", "1001", "1001", "1001", "0110"], "B": ["1110", "1001", "1110", "1001", "1110"],
    "A": ["0110", "1001", "1111", "1001", "1001"], "L": ["1000", "1000", "1000", "1000", "1111"],
    "Y": ["10001", "01010", "00100", "00100", "00100"], "N": ["1001", "1101", "1011", "1001", "1001"],
    "T": ["11111", "00100", "00100", "00100", "00100"], "H": ["1001", "1001", "1111", "1001", "1001"],
    "U": ["1001", "1001", "1001", "1001", "0110"], "M": ["10001", "11011", "10101", "10001", "10001"],
    " ": ["00", "00", "00", "00", "00"],
}

canvas = [[BACKGROUND] * SIZE for _ in range(SIZE)]


def rect(x0: int, y0: int, x1: int, y1: int, colour: str, radius: int = 0) -> None:
    """Filled rectangle, both corners inclusive; radius rounds the corners."""
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            dx = max(x0 + radius - x, x - (x1 - radius), 0)
            dy = max(y0 + radius - y, y - (y1 - radius), 0)
            if dx * dx + dy * dy <= radius * radius + radius * 0.6 and 0 <= x < SIZE and 0 <= y < SIZE:
                canvas[y][x] = colour


def text_width(content: str, scale: int) -> int:
    return (sum(len(FONT[ch][0]) for ch in content) + len(content) - 1) * scale


def text(x: int, y: int, content: str, colour: str, scale: int) -> int:
    """Draws `content` and returns the x after it."""
    for ch in content:
        for row, bits in enumerate(FONT[ch]):
            for col, bit in enumerate(bits):
                if bit == "1":
                    rect(x + col * scale, y + row * scale, x + (col + 1) * scale - 1, y + (row + 1) * scale - 1, colour)
        x += (len(FONT[ch][0]) + 1) * scale
    return x


def monitor() -> None:
    rect(54, 78, 73, 86, INK)                    # neck
    rect(56, 78, 71, 84, CASE)
    rect(38, 83, 89, 90, INK, radius=2)          # foot
    rect(40, 85, 87, 88, CASE, radius=1)
    rect(14, 6, 113, 80, INK, radius=8)          # casing
    rect(16, 8, 111, 78, CASE, radius=6)
    rect(22, 14, 105, 66, INK, radius=5)         # screen
    rect(24, 16, 103, 64, SCREEN, radius=4)
    for x in (24, 28, 32):                       # vents
        rect(x, 71, x + 1, 73, DETAIL)
    rect(100, 71, 102, 73, INK)                  # power light
    rect(101, 72, 101, 72, PHOSPHOR)


def kaomoji() -> None:
    """(・ω・) in 2-pixel strokes, centred on the screen."""
    def both(x0: int, y0: int, x1: int, y1: int) -> None:
        rect(x0, y0, x1, y1, PHOSPHOR)
        rect(SIZE - 1 - x1, y0, SIZE - 1 - x0, y1, PHOSPHOR)

    both(37, 27, 38, 28); both(35, 29, 36, 31); both(33, 32, 34, 47); both(35, 48, 36, 50); both(37, 51, 38, 52)   # ( )
    both(46, 36, 50, 40)                                                                                            # ・ ・
    for x, y in ((46, 36), (50, 36), (46, 40), (50, 40)):
        rect(x, y, x, y, SCREEN)
        rect(SIZE - 1 - x, y, SIZE - 1 - x, y, SCREEN)
    both(56, 37, 57, 38); both(55, 39, 56, 44); both(57, 45, 62, 46)                                                # ω
    rect(63, 41, 64, 44, PHOSPHOR)


def title() -> None:
    x = (SIZE - text_width("PROBABLY", 2)) // 2
    text(x, 98, "PROBABLY", CASE, 2)
    x = (SIZE - text_width("NOT HUMAN", 2)) // 2
    x = text(x, 112, "NOT ", PHOSPHOR, 2)
    text(x, 112, "HUMAN", CASE, 2)


def write_png(path: Path) -> None:
    raw = bytearray()
    for row in canvas:
        line = b"".join(bytes.fromhex(colour[1:]) * SCALE for colour in row)
        raw += (b"\0" + line) * SCALE

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    side = SIZE * SCALE
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", side, side, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def main() -> None:
    monitor()
    kaomoji()
    title()
    target = Path(__file__).resolve().parent / "preview.png"
    write_png(target)
    print(f"wrote {target} ({target.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
