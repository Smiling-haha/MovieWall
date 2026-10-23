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


def wheel(hwnd, x, y, notches):
    """notches>0 向上，<0 向下"""
    delta = int(notches * 120)
    lp = ((y & 0xFFFF) << 16) | (x & 0xFFFF)
    wp = ((delta & 0xFFFF) << 16)
    user32.PostMessageW(hwnd, 0x020A, wp, lp)   # WM_MOUSEWHEEL


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
    hwnd, title, rect = find()
    x, y = rect[0] + rect[2] // 2, rect[1] + rect[3] // 2
    print("win", rect, "at", x, y)

    foreground(hwnd)
    time.sleep(1.0)

    # 先把鼠标移到窗口中间，WPF 才知道滚轮作用在哪
    user32.SetCursorPos(x, y)
    time.sleep(0.3)

    on = sys.argv[1] if len(sys.argv) > 1 else "down"
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 5
    out = sys.argv[3] if len(sys.argv) > 3 else "../_shots/scroll.png"

    for _ in range(count):
        wheel(hwnd, x, y, -1 if on == "down" else 1)
        time.sleep(0.16)

    time.sleep(1.4)
    shot(hwnd, out)
    print("saved", out)
