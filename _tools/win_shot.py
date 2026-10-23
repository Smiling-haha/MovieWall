"""抓指定标题的窗口：优先 PrintWindow（不受遮挡影响），失败再回退到屏幕截图。"""
import ctypes
from ctypes import wintypes
import struct
import sys
import time
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

WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)


def enum_visible():
    out = []

    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n and user32.IsWindowVisible(hwnd):
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            r = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            out.append((hwnd, buf.value, (r.left, r.top, r.right - r.left, r.bottom - r.top)))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return out


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


def save_png(path, w, h, bgra):
    row = b"".join(bgra[i:i + 3][::-1] for i in range(0, len(bgra), 4))
    stride = w * 3
    scan = b"".join(b"\x00" + row[i * stride:(i + 1) * stride] for i in range(h))

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(scan, 6))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def render(hwnd, w, h, from_screen, src_x, src_y):
    src_dc = user32.GetDC(0) if from_screen else user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(src_dc)
    bmp = gdi32.CreateCompatibleBitmap(src_dc, w, h)
    gdi32.SelectObject(mem, bmp)

    if from_screen:
        gdi32.BitBlt(mem, 0, 0, w, h, src_dc, src_x, src_y, 0x00CC0020)
    else:
        user32.PrintWindow(hwnd, mem, 2)  # PW_RENDERFULLCONTENT

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h
    bi.biPlanes = 1
    bi.biBitCount = 32

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    data = buf.raw

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    if from_screen:
        user32.ReleaseDC(0, src_dc)
    else:
        user32.ReleaseDC(hwnd, src_dc)

    return data


if __name__ == "__main__":
    out, keyword = sys.argv[1], sys.argv[2]
    wins = [w for w in enum_visible() if keyword in w[1]]
    if not wins:
        print("NO WINDOW containing:", keyword)
        sys.exit(2)

    hwnd, title, rect = max(wins, key=lambda x: x[2][2] * x[2][3])
    print("found:", title, rect)

    user32.SetWindowPos(hwnd, -1, rect[0], rect[1], rect[2], rect[3], 0x0002 | 0x0001 | 0x0040)
    user32.ShowWindow(hwnd, 9)
    user32.SetForegroundWindow(hwnd)
    time.sleep(1.0)

    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top

    data = render(hwnd, w, h, False, 0, 0)
    mode = "PrintWindow"

    # 全黑说明 PrintWindow 没抓到（DirectComposition 的常见情况），退回屏幕抓取
    if not any(data[0:len(data):4000]):
        data = render(hwnd, w, h, True, r.left, r.top)
        mode = "screen"

    save_png(out, w, h, data)
    user32.SetWindowPos(hwnd, -2, 0, 0, 0, 0, 0x0002 | 0x0001)
    print(f"saved {out} via {mode} ({w}x{h})")
