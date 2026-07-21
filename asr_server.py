#!/usr/bin/env python3
"""
FocusCapture ASR Server v2.1 — SenseVoice + sherpa-onnx
用法: asr_server.py <model_dir>
模型: SenseVoice Small (INT8) + silero VAD
首次启动自动下载模型（如果缺失），优先 ModelScope 国内源，GitHub 备用
"""

import sys
import json
import os
import urllib.request
from pathlib import Path

__version__ = "2.1"

MODEL_DIR = sys.argv[1] if len(sys.argv) > 1 else "."

# ── 模型文件清单 ──
SENSEVOICE_MODEL = "model.int8.onnx"
SENSEVOICE_TOKENS = "tokens.txt"
VAD_FILE = "silero_vad.onnx"

# ── 下载源（按优先级排列，自动 fallback） ──
# 国内主源：gomodels/sherpa collection 一次包了 SenseVoice + VAD，CDN 是 cdn-lfs-cn-1.modelscope.cn
MS_GOMODELS_BASE = "https://www.modelscope.cn/models/gomodels/sherpa/resolve/master"
MS_GOMODELS = {
    SENSEVOICE_MODEL: f"{MS_GOMODELS_BASE}/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/{SENSEVOICE_MODEL}",
    SENSEVOICE_TOKENS: f"{MS_GOMODELS_BASE}/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/{SENSEVOICE_TOKENS}",
    VAD_FILE: f"{MS_GOMODELS_BASE}/vad/{VAD_FILE}",
}

# 备用源 1：pengzhendong 用户托管的 SenseVoice 模型（也走 ModelScope CDN）
MS_PENGZHENDONG_BASE = "https://www.modelscope.cn/models/pengzhendong/sherpa-onnx-sense-voice-zh-en-ja-ko-yue/resolve/master"
MS_PENGZHENDONG = {
    SENSEVOICE_MODEL: f"{MS_PENGZHENDONG_BASE}/{SENSEVOICE_MODEL}",
    SENSEVOICE_TOKENS: f"{MS_PENGZHENDONG_BASE}/{SENSEVOICE_TOKENS}",
}

# 备用源 2：GitHub Releases（境外，国外用户或国内兜底）
GH_BASE = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models"
GITHUB = {
    SENSEVOICE_MODEL: f"{GH_BASE}/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17.tar.bz2",
    SENSEVOICE_TOKENS: f"{GH_BASE}/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17.tar.bz2",  # 包含在 tar 里
    VAD_FILE: f"{GH_BASE}/{VAD_FILE}",
}


def emit(type_, text="", **kwargs):
    """输出 JSON 行到 stdout"""
    msg = {"type": type_, "text": text}
    msg.update(kwargs)
    print(json.dumps(msg, ensure_ascii=False), flush=True)


def _download_with_progress(url, out_path, label):
    """下载单个文件，带进度回调。返回 (success, error_msg)"""
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "FocusCapture/2.1"})
        with urllib.request.urlopen(req, timeout=300) as src:
            total = int(src.headers.get("Content-Length", 0))

            # 检查是否被重定向到 LFS 错误页（HTTP 200 但内容是 HTML 错误页）
            content_type = src.headers.get("Content-Type", "")
            if "text/html" in content_type and total < 1024:
                return False, f"重定向后返回 HTML 错误页 ({total}B)"

            downloaded = 0
            last_pct = -1
            with open(out_path, "wb") as dst:
                while True:
                    chunk = src.read(65536)
                    if not chunk:
                        break
                    dst.write(chunk)
                    downloaded += len(chunk)
                    if total > 0:
                        pct = min(99, int(downloaded * 100 / total))
                        if pct != last_pct and pct % 5 == 0:
                            emit("progress", f"下载 {label} {pct}%…", percent=pct)
                            last_pct = pct
        return True, ""
    except Exception as e:
        return False, f"{type(e).__name__}: {e}"


def download_file(filename, sources):
    """按 sources 列表顺序尝试下载，输出详细日志"""
    out_path = os.path.join(MODEL_DIR, filename)
    os.makedirs(MODEL_DIR, exist_ok=True)

    emit("status", f"正在下载 {filename}…")

    for i, url in enumerate(sources):
        source_name = (
            "ModelScope (gomodels)" if i == 0
            else "ModelScope (pengzhendong)" if i == 1
            else "GitHub" if "github" in url
            else f"源 {i+1}"
        )

        # 清理可能残留的部分文件
        if os.path.exists(out_path):
            try: os.remove(out_path)
            except: pass

        ok, err = _download_with_progress(url, out_path, filename)
        if ok:
            size_mb = os.path.getsize(out_path) / (1024 * 1024)
            emit("status", f"  ✓ {filename} ({size_mb:.0f}MB) 从 {source_name} 下载完成")
            return True

        emit("status", f"  ✗ {source_name} 失败: {err}")

    emit("error", f"所有下载源均失败，请检查网络后重试")
    raise RuntimeError(f"下载 {filename} 失败")


def check_and_download():
    """检查模型文件，缺失则下载"""
    # SenseVoice 模型（INT8）
    sv_path = Path(MODEL_DIR) / SENSEVOICE_MODEL
    sv_tokens = Path(MODEL_DIR) / SENSEVOICE_TOKENS
    vad_path = Path(MODEL_DIR) / VAD_FILE

    missing = []
    if not sv_path.is_file(): missing.append(SENSEVOICE_MODEL)
    if not sv_tokens.is_file(): missing.append(SENSEVOICE_TOKENS)
    if not vad_path.is_file(): missing.append(VAD_FILE)

    if not missing:
        return True  # 全部就绪

    emit("status", f"缺失 {len(missing)} 个模型文件，开始下载…")
    for f in missing:
        if f == SENSEVOICE_MODEL:
            sources = [MS_GOMODELS[SENSEVOICE_MODEL], MS_PENGZHENDONG[SENSEVOICE_MODEL], GITHUB[SENSEVOICE_MODEL]]
        elif f == SENSEVOICE_TOKENS:
            sources = [MS_GOMODELS[SENSEVOICE_TOKENS], MS_PENGZHENDONG[SENSEVOICE_TOKENS], GITHUB[SENSEVOICE_TOKENS]]
        elif f == VAD_FILE:
            sources = [MS_GOMODELS[VAD_FILE], GITHUB[VAD_FILE]]
        else:
            continue
        download_file(f, sources)

    # 最终检查（文件存在 + 大小合理）
    MIN_SIZES = {
        SENSEVOICE_MODEL: 200 * 1024 * 1024,  # INT8 模型约 228MB
        SENSEVOICE_TOKENS: 100 * 1024,         # ~308KB
        VAD_FILE: 1 * 1024 * 1024,             # ~2.2MB
    }
    invalid = []
    for fname, min_size in MIN_SIZES.items():
        fpath = Path(MODEL_DIR) / fname
        if not fpath.is_file() or fpath.stat().st_size < min_size:
            actual = fpath.stat().st_size if fpath.is_file() else 0
            invalid.append(f"{fname} (实际 {actual//1024}KB, 期望 ≥{min_size//1024//1024}MB)")

    if invalid:
        emit("error", f"文件不完整: {'; '.join(invalid)}")
        return False

    emit("status", "所有模型就绪")
    return True


def main():
    if not check_and_download():
        sys.exit(1)

    import numpy as np
    import sounddevice as sd
    import sherpa_onnx

    # ── 检查麦克风 ──
    try:
        devices = sd.query_devices()
    except Exception as e:
        emit("error", f"音频设备错误: {e}")
        sys.exit(1)

    if len(devices) == 0:
        emit("error", "未检测到麦克风设备")
        sys.exit(1)

    default_input = sd.default.device[0] or 0
    if default_input >= len(devices):
        default_input = 0
    device_name = str(devices[default_input]["name"])

    # ── 采样率 ──
    SAMPLE_RATE = 16000

    # ── VAD 配置 ──
    vad_config = sherpa_onnx.VadModelConfig()
    vad_config.silero_vad.model = os.path.join(MODEL_DIR, VAD_FILE)
    vad_config.silero_vad.threshold = 0.5
    vad_config.silero_vad.min_silence_duration = 1.5   # 1.5 秒静音 = 断句
    vad_config.silero_vad.min_speech_duration = 0.3     # 最少 0.3 秒才算有效语音
    vad_config.silero_vad.max_speech_duration = 30      # 最长 30 秒强制断句
    vad_config.sample_rate = SAMPLE_RATE

    vad = sherpa_onnx.VoiceActivityDetector(vad_config, buffer_size_in_seconds=60)

    # ── SenseVoice 离线识别器 ──
    recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
        model=os.path.join(MODEL_DIR, "model.int8.onnx"),
        tokens=os.path.join(MODEL_DIR, "tokens.txt"),
        num_threads=2,
        use_itn=True,  # 逆文本归一化 + 标点
    )

    emit("ready", device_name)

    # ── 状态变量 ──
    block_size = int(0.1 * SAMPLE_RATE)  # 100ms 块
    is_speaking = False  # 是否正在说话（用于状态指示）

    def callback(indata, frames, time_info, status):
        nonlocal is_speaking

        if status:
            # PortAudio 状态（如溢出），不发错误避免刷屏
            print(f"[portaudio] {status}", file=sys.stderr, flush=True)

        try:
            samples = indata[:, 0].copy()  # 单声道 float32

            # 音量指示（每块都发，C# 端会更新 ProgressBar）
            if samples.size > 0:
                rms = float(np.sqrt(np.mean(samples.astype(np.float64) ** 2)))
                if rms > 0.002:
                    emit("volume", "", value=min(1.0, rms * 8))

            vad.accept_waveform(samples)

            # 检测语音活动状态变化
            speech_now = vad.is_speech_detected()
            if speech_now and not is_speaking:
                is_speaking = True
                emit("partial", "正在识别…")
            elif not speech_now and is_speaking:
                is_speaking = False

            # 处理 VAD 切出的完整语音段
            # 关键：front 是 reference，valid 直到下次 VAD 方法调用
            # 必须先 np.array 复制数据，再 pop，否则 seg.samples 会被清空
            while not vad.empty():
                segment = vad.front  # property, 不要加括号
                seg_samples = np.array(segment.samples, dtype=np.float32)  # ← 立即复制
                vad.pop()  # ← 复制后再 pop

                if seg_samples.size < 1600:  # < 0.1s 跳过
                    continue

                stream = recognizer.create_stream()
                stream.accept_waveform(SAMPLE_RATE, seg_samples)
                recognizer.decode_stream(stream)
                text = stream.result.text.strip() if stream.result else ""

                if text:
                    emit("final", text)
                    emit("partial", "聆听中…")  # 一句结束，回到待命状态

        except Exception as e:
            # 回调里抛异常会被 CFFI 转成系统弹窗，吞掉并输出 stderr
            print(f"[callback error] {type(e).__name__}: {e}", file=sys.stderr, flush=True)
            import traceback
            traceback.print_exc(file=sys.stderr)
            try:
                emit("error", f"{type(e).__name__}: {e}")
            except:
                pass

    try:
        with sd.InputStream(
            samplerate=SAMPLE_RATE,
            channels=1,
            dtype=np.float32,
            blocksize=block_size,
            callback=callback,
        ):
            emit("status", "聆听中…")
            _ = sys.stdin.read(1)  # 等待 stdin 关闭（C# 端发停止信号）
    except KeyboardInterrupt:
        pass
    except Exception as e:
        emit("error", str(e))


if __name__ == "__main__":
    main()
