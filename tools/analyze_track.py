#!/usr/bin/env python3
"""Local track analysis sidecar for SpotifyWPF (Path B) — Slice 2 Classic + BeatThis.

Reads a WASAPI-captured WAV and writes TrackAnalysis JSON plus Classic beat-synchronous
feature vectors for the C# BeatGraphBuilder.

Beat times/downbeats: BeatThis (pip package or ONNX) as sole source when available.
Ellis DP (librosa.beat.beat_track) is fallback only — never merged with BeatThis.

Features: chroma (L2) + MFCC[1:] + RMS-dB → z-score → beat-sync median → stack_memory.

Usage:
    python analyze_track.py input.wav output.json [--track-id ID]
    python analyze_track.py input.wav output.json --model tools/models/beat_this_small0.onnx

Requires: librosa, soundfile, numpy
Optional: beat_this (PyTorch), onnxruntime (+ ONNX model file)
"""

from __future__ import print_function

import argparse
import json
import os
import sys

try:
    import numpy as np
    import librosa
    import soundfile as sf
except ImportError as exc:  # pragma: no cover
    sys.stderr.write(
        "Missing Python dependency: %s\nInstall with: pip install librosa soundfile\n" % exc)
    sys.exit(2)

HOP_LENGTH = 512
SAMPLE_RATE = 22050
SEGMENT_HOP_SEC = 0.25
SEGMENT_LEN_SEC = 0.75
STACK_STEPS = 4
ZSCORE_EPS = 1e-6
GAP_SPLIT_RATIO = 1.75
BEATTHIS_FPS = 50
BEATTHIS_HOP = 441  # 22050 / 50
F_MEASURE_TOLERANCE_SEC = 0.070


def scalar(value):
    return float(np.asarray(value).reshape(-1)[0])


def frame_slice(frame_times, start, end):
    return np.where((frame_times >= start) & (frame_times < end))[0]


def build_intervals(times, total_duration, confidence=0.8, downbeat_mask=None):
    intervals = []
    for i, start in enumerate(times):
        end = times[i + 1] if i + 1 < len(times) else total_duration
        duration = max(end - start, 0.0)
        if duration <= 0:
            continue
        item = {
            "start": round(float(start), 5),
            "duration": round(float(duration), 5),
            "confidence": float(confidence if not isinstance(confidence, (list, np.ndarray))
                                else confidence[i]),
        }
        if downbeat_mask is not None and i < len(downbeat_mask):
            item["isDownbeat"] = bool(downbeat_mask[i])
        intervals.append(item)
    return intervals


def zscore_rows(matrix, eps=ZSCORE_EPS):
    """Z-score each feature row over time; floor std so near-constant dims don't explode."""
    mean = matrix.mean(axis=1, keepdims=True)
    std = matrix.std(axis=1, keepdims=True)
    std = np.maximum(std, eps)
    return (matrix - mean) / std


def l2_normalize_columns(chroma, eps=1e-8):
    """Fix peak-norm silence amp: L2-normalize chroma frames instead of peak=1."""
    norms = np.linalg.norm(chroma, axis=0, keepdims=True)
    norms = np.maximum(norms, eps)
    return chroma / norms


def gap_split_beats(beat_times, ratio=GAP_SPLIT_RATIO):
    """Fill monster gaps (> ratio × median) with a local tempo grid. Does not merge trackers."""
    beat_times = np.asarray(beat_times, dtype=float)
    if beat_times.size < 3:
        return beat_times, 0

    gaps = np.diff(beat_times)
    positive = gaps[gaps > 1e-4]
    if positive.size == 0:
        return beat_times, 0

    median = float(np.median(positive))
    if median <= 0:
        return beat_times, 0

    limit = median * ratio
    filled = [float(beat_times[0])]
    inserts = 0

    for i in range(len(gaps)):
        gap = float(gaps[i])
        start = float(beat_times[i])
        end = float(beat_times[i + 1])
        if gap > limit:
            n = int(np.floor(gap / median))
            for k in range(1, n):
                t = start + k * median
                if t < end - 0.25 * median:
                    filled.append(t)
                    inserts += 1
        filled.append(end)

    return np.asarray(filled, dtype=float), inserts


def f_measure_times(ref, est, tol=F_MEASURE_TOLERANCE_SEC):
    """MIR-style F-measure between two beat time lists (±tol seconds)."""
    ref = np.asarray(ref, dtype=float)
    est = np.asarray(est, dtype=float)
    if ref.size == 0 or est.size == 0:
        return 0.0, 0.0, 0.0

    matched_ref = set()
    matched_est = set()
    for j, t in enumerate(est):
        i = int(np.argmin(np.abs(ref - t)))
        if abs(ref[i] - t) <= tol and i not in matched_ref:
            matched_ref.add(i)
            matched_est.add(j)

    tp = len(matched_est)
    precision = tp / float(len(est))
    recall = tp / float(len(ref))
    if precision + recall <= 0:
        return 0.0, precision, recall
    f = 2.0 * precision * recall / (precision + recall)
    return float(f), float(precision), float(recall)


def track_beats_dp(y, sr):
    tempo, beat_frames = librosa.beat.beat_track(y=y, sr=sr, hop_length=HOP_LENGTH)
    tempo = scalar(tempo)
    beat_times = librosa.frames_to_time(beat_frames, sr=sr, hop_length=HOP_LENGTH)
    return np.asarray(beat_times, dtype=float), float(tempo), np.zeros(len(beat_times), dtype=bool)


def track_beats_beatthis_package(wav_path, y, sr):
    from beat_this.inference import Audio2Beats

    tracker = Audio2Beats(checkpoint_path="final0", device="cpu", dbn=False)
    beats, downbeats = tracker(y, sr)
    beats = np.asarray(beats, dtype=float)
    downbeats = np.asarray(downbeats, dtype=float)
    down_mask = _downbeat_mask(beats, downbeats)
    tempo = _tempo_from_beats(beats)
    return beats, tempo, down_mask, "beatthis"


def _mel_spect_beatthis(y, sr):
    """Approximate BeatThis LogMelSpect with librosa (hop=441 → 50 fps)."""
    if sr != SAMPLE_RATE:
        y = librosa.resample(y, orig_sr=sr, target_sr=SAMPLE_RATE)
        sr = SAMPLE_RATE
    S = librosa.feature.melspectrogram(
        y=y,
        sr=sr,
        n_fft=1024,
        hop_length=BEATTHIS_HOP,
        fmin=30.0,
        fmax=11000.0,
        n_mels=128,
        power=1.0,
        norm="slaney",
        htk=False,
    )
    # torchaudio frame_length normalize ≈ divide by n_fft; close enough for peak picking
    S = S / float(1024)
    spect = np.log1p(1000.0 * S.T).astype(np.float32)  # (time, 128)
    return spect


def _peak_pick_logits(logits, fps=BEATTHIS_FPS):
    """Minimal postprocessor: max-pool ±3 frames (~70ms), keep logit > 0."""
    x = np.asarray(logits, dtype=np.float64).reshape(-1)
    if x.size == 0:
        return np.zeros(0, dtype=float)

    # reflect-pad then max filter width 7
    pad = 3
    padded = np.pad(x, (pad, pad), mode="constant", constant_values=-1000.0)
    window = np.lib.stride_tricks.sliding_window_view(padded, 7)
    local_max = window.max(axis=1)
    peaks = (x == local_max) & (x > 0.0)
    frames = np.flatnonzero(peaks)
    # dedupe adjacent
    kept = []
    for f in frames:
        if not kept or f - kept[-1] > 1:
            kept.append(int(f))
        else:
            # average toward stronger
            if x[f] > x[kept[-1]]:
                kept[-1] = int(f)
    return np.asarray(kept, dtype=float) / float(fps)


def track_beats_onnx(y, sr, model_path):
    import onnxruntime as ort

    spect = _mel_spect_beatthis(y, sr)
    # Process in chunks of 1500 frames with border 6 (mirrors BeatThis inference)
    chunk_size = 1500
    border = 6
    n = spect.shape[0]
    beat_logits = np.full(n, -1000.0, dtype=np.float32)
    down_logits = np.full(n, -1000.0, dtype=np.float32)

    sess = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
    input_name = sess.get_inputs()[0].name

    starts = list(range(-border, max(n - border, 1), chunk_size - 2 * border))
    if n > chunk_size - 2 * border:
        starts[-1] = n - (chunk_size - border)

    for start in starts:
        left_pad = max(0, -start)
        body_start = max(0, start)
        body_end = min(start + chunk_size, n)
        chunk = spect[body_start:body_end]
        right_pad = max(0, chunk_size - left_pad - chunk.shape[0])
        if left_pad or right_pad:
            chunk = np.pad(chunk, ((left_pad, right_pad), (0, 0)), mode="constant")
        chunk = chunk[np.newaxis, :, :].astype(np.float32)
        beat_out, down_out = sess.run(None, {input_name: chunk})
        beat_out = np.asarray(beat_out).reshape(-1)
        down_out = np.asarray(down_out).reshape(-1)
        # write interior (drop border)
        write_start = start + border
        write_end = start + chunk_size - border
        src_start = border
        src_end = chunk_size - border
        dst0 = max(0, write_start)
        dst1 = min(n, write_end)
        src0 = src_start + (dst0 - write_start)
        src1 = src0 + (dst1 - dst0)
        if dst1 > dst0 and src1 > src0:
            beat_logits[dst0:dst1] = beat_out[src0:src1]
            down_logits[dst0:dst1] = down_out[src0:src1]

    beat_times = _peak_pick_logits(beat_logits)
    down_times = _peak_pick_logits(down_logits)
    # snap downbeats to nearest beat
    if beat_times.size and down_times.size:
        snapped = []
        for d in down_times:
            snapped.append(beat_times[int(np.argmin(np.abs(beat_times - d)))])
        down_times = np.unique(np.asarray(snapped, dtype=float))

    down_mask = _downbeat_mask(beat_times, down_times)
    tempo = _tempo_from_beats(beat_times)
    return beat_times, tempo, down_mask, "beatthis-onnx"


def _downbeat_mask(beats, downbeats, tol=0.05):
    mask = np.zeros(len(beats), dtype=bool)
    if len(beats) == 0 or len(downbeats) == 0:
        # fall back: every 4th beat
        mask[::4] = True
        if mask.size:
            mask[0] = True
        return mask
    for d in downbeats:
        i = int(np.argmin(np.abs(beats - d)))
        if abs(beats[i] - d) <= tol:
            mask[i] = True
    if not mask.any():
        mask[::4] = True
        mask[0] = True
    return mask


def _tempo_from_beats(beats):
    if len(beats) < 2:
        return 120.0
    gaps = np.diff(beats)
    gaps = gaps[gaps > 1e-3]
    if gaps.size == 0:
        return 120.0
    return float(60.0 / np.median(gaps))


def resolve_beats(wav_path, y, sr, model_path=None):
    """BeatThis first; DP fallback. Never merges the two beat lists."""
    dp_times, dp_tempo, _ = track_beats_dp(y, sr)
    tracker = "librosa-dp"
    beat_times = dp_times
    tempo = dp_tempo
    down_mask = np.zeros(len(beat_times), dtype=bool)
    down_mask[::4] = True
    confidence = 0.55

    # 1) Official package
    try:
        beat_times, tempo, down_mask, tracker = track_beats_beatthis_package(wav_path, y, sr)
        confidence = 0.95
        sys.stderr.write("Beat tracker: beat_this package\n")
    except Exception as package_exc:
        sys.stderr.write("beat_this package unavailable (%s); trying ONNX…\n" % package_exc)
        # 2) ONNX model
        candidates = []
        if model_path:
            candidates.append(model_path)
        here = os.path.dirname(os.path.abspath(__file__))
        candidates.extend([
            os.path.join(here, "models", "beat_this_small0.onnx"),
            os.path.join(here, "models", "beat_this_final0.onnx"),
            os.path.join(here, "beat_this_small0.onnx"),
        ])
        onnx_ok = False
        for path in candidates:
            if path and os.path.isfile(path):
                try:
                    beat_times, tempo, down_mask, tracker = track_beats_onnx(y, sr, path)
                    confidence = 0.90
                    onnx_ok = True
                    sys.stderr.write("Beat tracker: ONNX (%s)\n" % path)
                    break
                except Exception as onnx_exc:
                    sys.stderr.write("ONNX beat tracking failed (%s): %s\n" % (path, onnx_exc))
        if not onnx_ok:
            beat_times, tempo, down_mask = dp_times, dp_tempo, down_mask
            tracker = "librosa-dp"
            confidence = 0.55
            sys.stderr.write("Beat tracker: librosa DP fallback\n")

    # Agreement vs DP (diagnostic only — do not blend times)
    f_meas, prec, rec = f_measure_times(dp_times, beat_times)
    agreement = {
        "fMeasure": round(f_meas, 4),
        "precision": round(prec, 4),
        "recall": round(rec, 4),
        "toleranceMs": int(F_MEASURE_TOLERANCE_SEC * 1000),
        "dpBeatCount": int(len(dp_times)),
        "beatCount": int(len(beat_times)),
    }

    prior_down_times = beat_times[down_mask] if (
        isinstance(down_mask, np.ndarray) and down_mask.size == len(beat_times) and down_mask.any()
    ) else beat_times[::4]

    beat_times, gap_inserts = gap_split_beats(beat_times)
    if gap_inserts:
        sys.stderr.write("Gap-split inserted %d beats (>%gx median)\n" % (gap_inserts, GAP_SPLIT_RATIO))

    down_mask = _downbeat_mask(beat_times, prior_down_times)
    agreement["beatCount"] = int(len(beat_times))

    return beat_times, tempo, down_mask, confidence, tracker, agreement, gap_inserts


def build_classic_features(y, sr, beat_times):
    """Median-synced, z-scored, stacked beat vectors (Slice 2 Classic)."""
    chroma = librosa.feature.chroma_cqt(y=y, sr=sr, hop_length=HOP_LENGTH)
    chroma = l2_normalize_columns(chroma)
    mfcc = librosa.feature.mfcc(y=y, sr=sr, n_mfcc=13, hop_length=HOP_LENGTH)
    mfcc = mfcc[1:, :]  # drop MFCC-0 (loudness)
    rms = librosa.feature.rms(y=y, hop_length=HOP_LENGTH)[0]
    rms_db = librosa.amplitude_to_db(rms, ref=np.max(rms) if np.max(rms) > 0 else 1.0)

    F = np.vstack([chroma, mfcc, rms_db.reshape(1, -1)])
    F = zscore_rows(F)

    beat_frames = librosa.time_to_frames(beat_times, sr=sr, hop_length=HOP_LENGTH)
    beat_frames = librosa.util.fix_frames(beat_frames, x_max=F.shape[1])
    # sync expects beat frames as boundaries; pad end
    if beat_frames.size == 0 or beat_frames[-1] < F.shape[1] - 1:
        beat_frames = np.concatenate([beat_frames, [F.shape[1]]])

    Fb = librosa.util.sync(F, beat_frames, aggregate=np.median, pad=False)
    # Fb columns correspond to intervals between consecutive beat_frames.
    # Align to beat count: use one vector per beat (column i = beat i).
    n_beats = len(beat_times)
    if Fb.shape[1] > n_beats:
        Fb = Fb[:, :n_beats]
    elif Fb.shape[1] < n_beats:
        pad = np.repeat(Fb[:, -1:], n_beats - Fb.shape[1], axis=1)
        Fb = np.concatenate([Fb, pad], axis=1)

    Fs = librosa.feature.stack_memory(Fb, n_steps=STACK_STEPS)

    beat_features = [[round(float(v), 6) for v in Fb[:, i]] for i in range(n_beats)]
    stacked = [[round(float(v), 6) for v in Fs[:, i]] for i in range(n_beats)]
    return beat_features, stacked, chroma, mfcc, rms_db


def build_overlapping_segments(frame_times, chroma, mfcc12, rms_db, duration):
    """Kept for Path-A-compatible ring/legacy consumers; Classic graph prefers beatFeatures."""
    segments = []
    start = 0.0
    while start < duration - 0.05:
        end = min(start + SEGMENT_LEN_SEC, duration)
        idx = frame_slice(frame_times, start, end)
        if idx.size == 0:
            start += SEGMENT_HOP_SEC
            continue

        chroma_mean = chroma[:, idx].mean(axis=1)
        norm = np.linalg.norm(chroma_mean)
        if norm > 1e-8:
            chroma_mean = chroma_mean / norm
        pitches = [round(float(v), 4) for v in chroma_mean]
        timbre = [round(float(v), 4) for v in mfcc12[:, idx].mean(axis=1)]

        rms_idx = idx[idx < len(rms_db)]
        if rms_idx.size == 0:
            loudness_start = loudness_max = -60.0
            loudness_max_time = 0.0
        else:
            seg_db = rms_db[rms_idx]
            loudness_start = float(seg_db[0])
            max_pos = int(np.argmax(seg_db))
            loudness_max = float(seg_db[max_pos])
            loudness_max_time = float(frame_times[rms_idx[max_pos]] - start)

        segments.append({
            "start": round(float(start), 5),
            "duration": round(float(end - start), 5),
            "confidence": 1.0,
            "loudnessStart": round(loudness_start, 3),
            "loudnessMax": round(loudness_max, 3),
            "loudnessMaxTime": round(loudness_max_time, 5),
            "pitches": pitches,
            "timbre": timbre,
        })
        start += SEGMENT_HOP_SEC
    return segments


def bars_from_downbeats(beat_times, down_mask, duration):
    down_times = beat_times[down_mask] if down_mask.any() else beat_times[::4]
    if down_times.size == 0:
        down_times = beat_times[::4]
    return build_intervals(down_times, duration, confidence=0.85)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_wav")
    parser.add_argument("output_json")
    parser.add_argument("--track-id", default="")
    parser.add_argument("--model", default="", help="Path to BeatThis ONNX model (optional)")
    args = parser.parse_args()

    try:
        y, sr_native = sf.read(args.input_wav, dtype="float32", always_2d=True)
        y = y.mean(axis=1)
        if sr_native != SAMPLE_RATE:
            y = librosa.resample(y, orig_sr=sr_native, target_sr=SAMPLE_RATE)
        sr = SAMPLE_RATE
    except Exception:
        try:
            y, sr = librosa.load(args.input_wav, sr=SAMPLE_RATE, mono=True)
        except Exception as exc:
            sys.stderr.write("Could not read %s: %s\n" % (args.input_wav, exc))
            sys.exit(3)

    if y.size == 0:
        sys.stderr.write("Input audio is empty (%s).\n" % args.input_wav)
        sys.exit(3)

    duration = float(len(y)) / sr

    beat_times, tempo, down_mask, beat_conf, tracker, agreement, gap_inserts = resolve_beats(
        args.input_wav, y, sr, model_path=args.model or None)

    if len(beat_times) < 8:
        sys.stderr.write("Too few beats detected (%d).\n" % len(beat_times))
        sys.exit(4)

    beat_features, stacked, chroma, mfcc12, rms_db = build_classic_features(y, sr, beat_times)
    frame_times = librosa.frames_to_time(np.arange(chroma.shape[1]), sr=sr, hop_length=HOP_LENGTH)
    # segments still use 12 MFCCs including a padded MFCC-0 slot for schema compat
    mfcc_seg = librosa.feature.mfcc(y=y, sr=sr, n_mfcc=12, hop_length=HOP_LENGTH)
    segments = build_overlapping_segments(frame_times, chroma, mfcc_seg, rms_db, duration)

    beats = build_intervals(beat_times, duration, confidence=beat_conf, downbeat_mask=down_mask)
    bars = bars_from_downbeats(beat_times, down_mask, duration)

    tatum_times = []
    for beat in beats:
        tatum_times.append(beat["start"])
        tatum_times.append(beat["start"] + beat["duration"] / 2.0)
    tatums = build_intervals(np.array(tatum_times), duration, confidence=0.4)

    section_count = max(2, min(12, int(duration // 30) + 1))
    sections = []
    try:
        boundaries = librosa.segment.agglomerative(chroma, section_count)
        boundary_times = librosa.frames_to_time(boundaries, sr=sr, hop_length=HOP_LENGTH)
        for interval in build_intervals(boundary_times, duration, confidence=0.5):
            idx = frame_slice(frame_times, interval["start"],
                              interval["start"] + interval["duration"])
            rms_idx = idx[idx < len(rms_db)]
            sections.append({
                "start": interval["start"],
                "duration": interval["duration"],
                "confidence": interval["confidence"],
                "tempo": round(tempo, 3),
                "key": -1,
                "mode": -1,
                "loudness": round(float(rms_db[rms_idx].mean()) if rms_idx.size else -60.0, 3),
            })
    except Exception as exc:
        sys.stderr.write("Section segmentation failed: %s\n" % exc)

    key = int(np.argmax(chroma.mean(axis=1)))

    analysis = {
        "trackId": args.track_id,
        "sourceType": "local",
        "durationSec": round(duration, 5),
        "tempo": round(tempo, 3),
        "key": key,
        "mode": -1,
        "loudness": round(float(rms_db.mean()), 3),
        "bars": bars,
        "beats": beats,
        "tatums": tatums,
        "sections": sections,
        "segments": segments,
        "beatTracker": tracker,
        "stackSteps": STACK_STEPS,
        "gapSplitInserts": int(gap_inserts),
        "dpAgreement": agreement,
        "beatFeatures": beat_features,
        "stackedFeatures": stacked,
    }

    with open(args.output_json, "w") as handle:
        json.dump(analysis, handle)

    print(
        "Wrote %s (%d beats, %d segments, tracker=%s, stack=%d, F=%.3f)"
        % (args.output_json, len(beats), len(segments), tracker, STACK_STEPS,
           agreement["fMeasure"])
    )


if __name__ == "__main__":
    main()
