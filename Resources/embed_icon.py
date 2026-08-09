# 一次性脚本：嵌入 app.ico 到 FocusCapture.exe
# .NET 8 SDK 不自动嵌入 ApplicationIcon 到 apphost，需手动 UpdateResource
# 用法：python Resources\embed_icon.py （从项目根目录）
import ctypes, struct, sys
from ctypes import wintypes
from pathlib import Path

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
k32.BeginUpdateResourceW.argtypes = [wintypes.LPCWSTR, wintypes.BOOL]
k32.BeginUpdateResourceW.restype = wintypes.HANDLE
k32.UpdateResourceW.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, wintypes.WORD, ctypes.c_void_p, wintypes.DWORD]
k32.UpdateResourceW.restype = wintypes.BOOL
k32.EndUpdateResourceW.argtypes = [wintypes.HANDLE, wintypes.BOOL]
k32.EndUpdateResourceW.restype = wintypes.BOOL

base = Path(__file__).resolve().parent.parent  # 项目根目录
ico = (base / 'Resources' / 'app.ico').read_bytes()
exe = base / 'bin' / 'Release' / 'net8.0-windows' / 'FocusCapture.exe'
if not exe.exists():
    print(f"跳过（exe 不存在: {exe}）")
    sys.exit(0)

# 解析 ICO
count = struct.unpack_from('<H', ico, 4)[0]
entries = []
for i in range(count):
    off = 6 + i*16
    w, h, cc, _, planes, bpp, sz, data_off = struct.unpack_from('<BBBBHHII', ico, off)
    if w == 0: w = 256
    if h == 0: h = 256
    entries.append((w, h, bpp, sz, ico[data_off:data_off+sz]))
print(f"ICO: {count} entries")
for w, h, bpp, sz, _ in entries:
    print(f"  {w}x{h} bpp={bpp} sz={sz}")

# 构造 RT_ICON_GROUP 数据（ICONDIR + entries，entries 最后一字段是 icon id 而非 offset）
group_data = struct.pack('<HHH', 0, 1, count)
for idx, (w, h, bpp, sz, _) in enumerate(entries, start=1):
    # ICO 约定：BYTE 类型 width/height = 0 表示 256
    w_b = 0 if w == 256 else w
    h_b = 0 if h == 256 else h
    group_data += struct.pack('<BBBBHHII', w_b, h_b, 0, 0, 1, bpp, sz, idx)

# 嵌入
h = k32.BeginUpdateResourceW(str(exe), False)
if not h: raise OSError(f'BeginUpdateResourceW: {ctypes.get_last_error()}')

# RT_ICON=3, RT_ICON_GROUP=14, LANG_NEUTRAL=0
ok = k32.UpdateResourceW(h, 3, 14, 0, ctypes.c_char_p(group_data), len(group_data))
if not ok: raise OSError(f'UpdateResourceW group: {ctypes.get_last_error()}')

for idx, (_, _, _, sz, blob) in enumerate(entries, start=1):
    buf = ctypes.create_string_buffer(blob)
    ok = k32.UpdateResourceW(h, 3, idx, 0, buf, sz)
    if not ok: raise OSError(f'UpdateResourceW icon {idx}: {ctypes.get_last_error()}')

ok = k32.EndUpdateResourceW(h, False)
if not ok: raise OSError(f'EndUpdateResourceW: {ctypes.get_last_error()}')

print(f"OK: {exe} ({exe.stat().st_size} bytes)")