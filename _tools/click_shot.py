"""置顶窗口 -> 模拟点击某个相对坐标 -> 等待 -> PrintWindow 截图。"""
import ctypes
from ctypes import wintypes
import sys
import time

import win_shot as ws

user32 = ctypes.windll.user32

if __name__ == "__main__":
    out, keyword = sys.argv[1], sys.argv[2]
    rel_x, rel_y = int(sys.argv[3]), int(sys.argv[4])
    wait = float(sys.argv[5]) if len(sys.argv) > 5 else 0.9

    wins = [w for w in ws.enum_visible() if keyword in w[1]]
    if not wins:
        print("NO WINDOW")
        sys.exit(2)

    hwnd, title, rect = max(wins, key=lambda x: x[2][2] * x[2][3])
    print("window:", title, rect)

    user32.SetWindowPos(hwnd, -1, rect[0], rect[1], rect[2], rect[3], 0x0002 | 0x0001 | 0x0040)
    user32.ShowWindow(hwnd, 9)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.8)

    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))

    ax, ay = r.left + rel_x, r.top + rel_y
    print(f"click at ({ax}, {ay})")
    user32.SetCursorPos(ax, ay)
    time.sleep(0.25)
    user32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    time.sleep(0.06)
    user32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP

    time.sleep(wait)

    w, h = r.right - r.left, r.bottom - r.top
    data = ws.render(hwnd, w, h, False, 0, 0)
    mode = "PrintWindow"
    if not any(data[0:len(data):4000]):
        data = ws.render(hwnd, w, h, True, r.left, r.top)
        mode = "screen"

    ws.save_png(out, w, h, data)
    user32.SetWindowPos(hwnd, -2, 0, 0, 0, 0, 0x0002 | 0x0001)
    print(f"saved {out} via {mode}")
