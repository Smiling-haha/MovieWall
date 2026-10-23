"""用 GDI 抓屏并写成 PNG（不依赖 Pillow）。"""
import ctypes
from ctypes import wintypes
import struct
import sys
import zlib

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        user32.SetProcessDPIAware()
    except Exception:
        pass


def find_window(title):
    hwnd = user32.FindWindowW(None, title)
    if hwnd:
        user32.ShowWindow(hwnd, 9)      # SW_RESTORE
        user32.BringWindowToTop(hwnd)
        user32.SetForegroundWindow(hwnd)
    return hwnd


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


def grab(path, title=None):
    if title:
        hwnd = find_window(title)
        print("hwnd:", hwnd)

    x = user32.GetSystemMetrics(76)
    y = user32.GetSystemMetrics(77)
    w = user32.GetSystemMetrics(78)
    h = user32.GetSystemMetrics(79)
    if w <= 0 or h <= 0:
        w, h, x, y = 1920, 1080, 0, 0

    hdc = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    size = w * h * 4
    buf = ctypes.create_string_buffer(size)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

    raw = buf.raw
    # BGRA -> RGB
    row = b"".join(raw[i:i + 3][::-1] for i in range(0, len(raw), 4))
    stride = w * 3
    scan = b"".join(b"\x00" + row[i * stride:(i + 1) * stride] for i in range(h))

    def chunk(typ, data):
        return (struct.pack(">I", len(data)) + typ + data
                + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF))

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(scan, 6))
    png += chunk(b"IEND", b"")

    with open(path, "wb") as f:
        f.write(png)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, hdc)
    print("saved", path, w, "x", h)


if __name__ == "__main__":
    grab(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
