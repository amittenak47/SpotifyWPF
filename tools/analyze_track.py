#!/usr/bin/env python3
"""Local track analysis sidecar for SpotifyWPF (Path B).

Reads a WAV captured via WASAPI loopback and writes analysis JSON in the same
shape as SpotifyWPF's TrackAnalysis model (a normalized subset of Spotify's
audio-analysis schema): beats/bars/tatums/sections plus per-beat segments with
12-bin chroma ("pitches") and 12 MFCCs ("timbre").

Usage:
    python analyze_track.py input.wav output.json [--track-id ID]

Requires: librosa, soundfile, numpy  (pip install librosa soundfile)
"""

import argparse
import json
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
SAMPLE_RATE = 22050  # matches the rate Spotify's analyzer used
SEGMENT_HOP_SEC = 0.25
SEGMENT_LEN_SEC = 0.75
MAX_RING_SEGMENTS = 48


def scalar(value):
    """librosa APIs return numpy scalars/0-d arrays in various versions."""
    return float(np.asarray(value).reshape(-1)[0])


def frame_slice(frame_times, start, end):
    """Indices of analysis frames falling inside [start, end)."""
    return np.where((frame_times >= start) & (frame_times < end))[0]


def build_intervals(times, total_duration, confidence=0.8):
    intervals = []
    for i, start in enumerate(times):
        end = times[i + 1] if i + 1 < len(times) else total_duration
        duration = max(end - start, 0.0)
        if duration <= 0:
            continue
        intervals.append({
            "start": round(float(start), 5),
            "duration": round(float(duration), 5),
            "confidence": confidence,
        })
    return intervals


def build_overlapping_segments(frame_times, chroma, mfcc, rms_db, duration):
    """Echo Nest-style overlapping windows (not one segment per beat)."""
    segments = []
    start = 0.0

    while start < duration - 0.05:
        end = min(start + SEGMENT_LEN_SEC, duration)
        idx = frame_slice(frame_times, start, end)

        if idx.size == 0:
            start += SEGMENT_HOP_SEC
            continue

        chroma_mean = chroma[:, idx].mean(axis=1)
        peak = chroma_mean.max()
        if peak > 0:
            chroma_mean = chroma_mean / peak
        pitches = [round(float(v), 4) for v in chroma_mean]
        timbre = [round(float(v), 4) for v in mfcc[:, idx].mean(axis=1)]

        rms_idx = idx[idx < len(rms_db)]
        if rms_idx.size == 0:
            loudness_start = -60.0
            loudness_max = -60.0
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_wav")
    parser.add_argument("output_json")
    parser.add_argument("--track-id", default="")
    args = parser.parse_args()

    try:
        y, sr_native = sf.read(args.input_wav, dtype="float32", always_2d=True)
        y = y.mean(axis=1)
        if sr_native != SAMPLE_RATE:
            y = librosa.resample(y, orig_sr=sr_native, target_sr=SAMPLE_RATE)
        sr = SAMPLE_RATE
    except Exception:
        y, sr = librosa.load(args.input_wav, sr=SAMPLE_RATE, mono=True)
    except Exception as exc:
        sys.stderr.write("Could not read %s: %s\n" % (args.input_wav, exc))
        sys.exit(3)

    if y.size == 0:
        sys.stderr.write("Input audio is empty.\n")
        sys.exit(3)

    duration = float(len(y)) / sr

    tempo, beat_frames = librosa.beat.beat_track(y=y, sr=sr, hop_length=HOP_LENGTH)
    tempo = scalar(tempo)
    beat_times = librosa.frames_to_time(beat_frames, sr=sr, hop_length=HOP_LENGTH)

    if len(beat_times) < 8:
        sys.stderr.write("Too few beats detected (%d) to build a useful analysis.\n" % len(beat_times))
        sys.exit(4)

    chroma = librosa.feature.chroma_cqt(y=y, sr=sr, hop_length=HOP_LENGTH)
    mfcc = librosa.feature.mfcc(y=y, sr=sr, n_mfcc=12, hop_length=HOP_LENGTH)
    rms = librosa.feature.rms(y=y, hop_length=HOP_LENGTH)[0]
    rms_db = librosa.amplitude_to_db(rms, ref=np.max(rms) if np.max(rms) > 0 else 1.0)
    frame_times = librosa.frames_to_time(np.arange(chroma.shape[1]), sr=sr, hop_length=HOP_LENGTH)

    beats = build_intervals(beat_times, duration)

    segments = build_overlapping_segments(frame_times, chroma, mfcc, rms_db, duration)

    # Bars: every 4 beats (crude but adequate for position-in-bar weighting).
    bar_times = beat_times[::4]
    bars = build_intervals(bar_times, duration, confidence=0.5)

    # Tatums: each beat halved.
    tatum_times = []
    for beat in beats:
        tatum_times.append(beat["start"])
        tatum_times.append(beat["start"] + beat["duration"] / 2.0)
    tatums = build_intervals(np.array(tatum_times), duration, confidence=0.4)

    # Sections: agglomerative segmentation over chroma into ~1 section per 30s.
    section_count = max(2, min(12, int(duration // 30) + 1))
    sections = []
    try:
        boundaries = librosa.segment.agglomerative(chroma, section_count)
        boundary_times = librosa.frames_to_time(boundaries, sr=sr, hop_length=HOP_LENGTH)
        for interval in build_intervals(boundary_times, duration, confidence=0.5):
            idx = frame_slice(frame_times, interval["start"], interval["start"] + interval["duration"])
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
        sys.stderr.write("Section segmentation failed (continuing without sections): %s\n" % exc)

    # Rough global key: strongest mean chroma bin. Mode is not estimated.
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
    }

    with open(args.output_json, "w") as handle:
        json.dump(analysis, handle)

    print("Wrote %s (%d beats, %d segments)" % (args.output_json, len(beats), len(segments)))


if __name__ == "__main__":
    main()
