# 图标嵌入脚本（发布版）：把 app.ico 嵌入指定 exe
# 用法：python embed_icon_publish.py <exe路径>
# 背景：.NET 8 单文件发布的 apphost 不自动带 ApplicationIcon，
#       csproj 的 PostBuildEvent 只处理编译产物，发布产物需本脚本补嵌。
import ctypes, struct, sys
from pathlib import Path

if len(sys.argv) < 2:
    print("用法: python embed_icon_publish.py <exe路径>")
    sys.exit(1)

exe = Path(sys.argv[1]).resolve()
if not exe.exists():
    print(f"exe 不存在: {exe}")
    sys.exit(1)

ico_path = Path(__file__).resolve().parent / 'app.ico'
if not ico_path.exists():
    print(f"app.ico 不存在: {ico_path}")
    sys.exit(1)

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
k32.BeginUpdateResourceW.argtypes = [ctypes.c_wchar_p, ctypes.c_bool]
k32.BeginUpdateResourceW.restype = ctypes.c_void_p
k32.UpdateResourceW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ushort, ctypes.c_void_p, ctypes.c_uint32]
k32.UpdateResourceW.restype = ctypes.c_bool
k32.EndUpdateResourceW.argtypes = [ctypes.c_void_p, ctypes.c_bool]
k32.EndUpdateResourceW.restype = ctypes.c_bool

ico = ico_path.read_bytes()
count = struct.unpack_from('<H', ico, 4)[0]
entries = []
for i in range(count):
    off = 6 + i * 16
    w, h, cc, _, planes, bpp, sz, data_off = struct.unpack_from('<BBBBHHII', ico, off)
    if w == 0: w = 256
    if h == 0: h = 256
    entries.append((w, h, bpp, sz, ico[data_off:data_off + sz]))

print(f"ICO: {count} entries")
for w, h, bpp, sz, _ in entries:
    print(f"  {w}x{h} bpp={bpp} sz={sz}")

group_data = struct.pack('<HHH', 0, 1, count)
for idx, (w, h, bpp, sz, _) in enumerate(entries, start=1):
    w_b = 0 if w == 256 else w
    h_b = 0 if h == 256 else h
    group_data += struct.pack('<BBBBHHII', w_b, h_b, 0, 0, 1, bpp, sz, idx)

h = k32.BeginUpdateResourceW(str(exe), False)
if not h:
    raise OSError(f'BeginUpdateResourceW failed: {ctypes.get_last_error()}')

# RT_ICON=3, RT_ICON_GROUP=14, LANG_NEUTRAL=0
ok = k32.UpdateResourceW(h, 3, 14, 0, ctypes.c_char_p(group_data), len(group_data))
if not ok:
    raise OSError(f'UpdateResourceW group failed: {ctypes.get_last_error()}')

for idx, (_, _, _, sz, blob) in enumerate(entries, start=1):
    buf = ctypes.create_string_buffer(blob)
    ok = k32.UpdateResourceW(h, 3, idx, 0, buf, sz)
    if not ok:
        raise OSError(f'UpdateResourceW icon {idx} failed: {ctypes.get_last_error()}')

ok = k32.EndUpdateResourceW(h, False)
if not ok:
    raise OSError(f'EndUpdateResourceW failed: {ctypes.get_last_error()}')

print(f"OK: {exe} ({exe.stat().st_size} bytes)")
