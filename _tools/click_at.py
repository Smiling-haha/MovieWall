import ctypes
import sys
import time
from ctypes import wintypes

sys.path.insert(0, '.')
import win_shot as ws

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
dwm = ctypes.windll.dwmapi


def find(title_part="私人影院"):
    wins = [w for w in ws.enum_visible() if title_part in w[1]]
    return max(wins, key=lambda x: x[2][2] * x[2][3])


def foreground(hwnd):
    fg = user32.GetForegroundWindow()
    ft = user32.GetWindowThreadProcessId(fg, None)
    mt = kernel32.GetCurrentThreadId()
    user32.AttachThreadInput(mt, ft, True)
    user32.BringWindowToTop(hwnd)
    user32.SetForegroundWindow(hwnd)
    user32.SetFocus(hwnd)
    user32.AttachThreadInput(mt, ft, False)


def click(hwnd, cx, cy):
    """cx, cy 是客户区坐标"""
    lp = ((cy & 0xFFFF) << 16) | (cx & 0xFFFF)
    user32.PostMessageW(hwnd, 0x0200, 0, lp)          # WM_MOUSEMOVE
    time.sleep(0.10)
    user32.PostMessageW(hwnd, 0x0201, 0x0001, lp)     # WM_LBUTTONDOWN
    time.sleep(0.08)
    user32.PostMessageW(hwnd, 0x0202, 0, lp)          # WM_LBUTTONUP


def shot(hwnd, path):
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    try:
        dwm.DwmFlush()
    except Exception:
        pass
    ws.save_png(path, w, h, ws.render(hwnd, w, h, False, 0, 0))


if __name__ == "__main__":
    cx = int(sys.argv[1])
    cy = int(sys.argv[2])
    out = sys.argv[3] if len(sys.argv) > 3 else "../_shots/click.png"

    hwnd, title, rect = find()
    print(f"win {rect} click client ({cx},{cy})")
    foreground(hwnd)
    time.sleep(0.8)

    user32.SetCursorPos(rect[0] + cx, rect[1] + cy)
    time.sleep(0.3)
    click(hwnd, cx, cy)
    time.sleep(1.6)
    shot(hwnd, out)
    print("saved", out)
