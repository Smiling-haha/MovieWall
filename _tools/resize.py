import ctypes
import sys
import time
from ctypes import wintypes

sys.path.insert(0, '.')
import win_shot as ws

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
dwm = ctypes.windll.dwmapi

SWP_NOZORDER = 0x0004
SWP_NOACTIVATE = 0x0010
SWP_NOSENDCHANGING = 0x0400
HWND_TOP = 0


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
    w = int(sys.argv[1])
    h = int(sys.argv[2])
    out = sys.argv[3]

    hwnd, title, rect = find()
    print("before", rect)
    foreground(hwnd)
    time.sleep(0.6)

    user32.SetWindowPos(hwnd, HWND_TOP, rect[0], rect[1], w, h,
                        SWP_NOACTIVATE | SWP_NOSENDCHANGING)
    time.sleep(2.0)

    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    print("after", (r.left, r.top, r.right - r.left, r.bottom - r.top))

    shot(hwnd, out)
    print("saved", out)
