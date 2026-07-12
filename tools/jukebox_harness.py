#!/usr/bin/env python3
"""Offline Slice 2 harness — measures Classic graph quality from analysis JSON.

Usage:
    python tools/jukebox_harness.py path/to/analysis.json [--k 6] [--percentile 55] [--min-jump 8]

Reports: dead-beat rate, jump-length distribution, continuation error (stack i+1 vs j),
mutual-kNN coverage, optional DP↔BeatThis F-measure from analysis.dpAgreement.
"""

from __future__ import print_function

import argparse
import json
import math
import sys

try:
    import numpy as np
except ImportError:
    sys.stderr.write("numpy required\n")
    sys.exit(2)


def load_vectors(analysis):
    stacked = analysis.get("stackedFeatures") or []
    beats = analysis.get("beats") or []
    if not stacked or len(stacked) != len(beats):
        sys.stderr.write("Analysis missing Classic stackedFeatures (re-run analyze_track.py).\n")
        sys.exit(3)
    return np.asarray(stacked, dtype=float), beats


def euclid(a, b):
    d = a - b
    return float(np.sqrt(np.mean(d * d)))


def phase_penalty(i, j, beats, mode="soft"):
    if mode == "off":
        return 0.0
    # Prefer isDownbeat-derived index-in-bar if present via running count
    return 0.0  # harness focuses on acoustic continuation; C# applies phase


def build_knn(vectors, beats, k=6, percentile=55.0, min_jump=8):
    n = len(vectors)
    pairs = []
    dist = np.full((n, n), np.inf)
    for i in range(n):
        for j in range(i + 1, n):
            if abs(i - j) < min_jump:
                continue
            d = euclid(vectors[i], vectors[j])
            dist[i, j] = dist[j, i] = d
            pairs.append(d)

    if not pairs:
        return [[] for _ in range(n)], 0.0

    thr = float(np.percentile(pairs, percentile))
    neighbors = []
    for i in range(n):
        cands = [(j, dist[i, j]) for j in range(n)
                 if abs(i - j) >= min_jump and dist[i, j] <= thr]
        cands.sort(key=lambda x: x[1])
        neighbors.append(cands[:k])
    return neighbors, thr


def mutual_coverage(neighbors):
    n = len(neighbors)
    mutual = 0
    edged = 0
    for i in range(n):
        dests = {j for j, _ in neighbors[i]}
        if dests:
            edged += 1
        for j in dests:
            if any(di == i for di, _ in neighbors[j]):
                mutual += 1
    # each mutual edge counted twice
    return edged / float(n), (mutual / 2.0) / float(max(1, edged))


def continuation_error(vectors, neighbors):
    """Mean ||stack(i+1) - stack(j)|| for edges i→j (splice continuation)."""
    errors = []
    n = len(vectors)
    for i, edges in enumerate(neighbors):
        if i + 1 >= n:
            continue
        nxt = vectors[i + 1]
        for j, _ in edges:
            errors.append(euclid(nxt, vectors[j]))
    if not errors:
        return None
    return float(np.mean(errors)), float(np.median(errors))


def jump_length_stats(neighbors):
    lengths = [abs(i - j) for i, edges in enumerate(neighbors) for j, _ in edges]
    if not lengths:
        return {}
    arr = np.asarray(lengths, dtype=float)
    return {
        "count": int(arr.size),
        "mean": round(float(arr.mean()), 2),
        "median": round(float(np.median(arr)), 2),
        "p90": round(float(np.percentile(arr, 90)), 2),
        "min": int(arr.min()),
        "max": int(arr.max()),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("analysis_json")
    parser.add_argument("--k", type=int, default=6)
    parser.add_argument("--percentile", type=float, default=55.0)
    parser.add_argument("--min-jump", type=int, default=8)
    args = parser.parse_args()

    with open(args.analysis_json) as handle:
        analysis = json.load(handle)

    vectors, beats = load_vectors(analysis)
    neighbors, thr = build_knn(vectors, beats, args.k, args.percentile, args.min_jump)

    n = len(neighbors)
    dead = sum(1 for edges in neighbors if not edges)
    coverage, mutual_frac = mutual_coverage(neighbors)
    cont = continuation_error(vectors, neighbors)
    jumps = jump_length_stats(neighbors)

    report = {
        "trackId": analysis.get("trackId"),
        "beatTracker": analysis.get("beatTracker"),
        "beats": n,
        "gapSplitInserts": analysis.get("gapSplitInserts", 0),
        "dpAgreement": analysis.get("dpAgreement"),
        "qualityPercentile": args.percentile,
        "distanceThreshold": round(thr, 6),
        "deadBeatRate": round(dead / float(n), 4),
        "deadBeats": dead,
        "branchableCoverage": round(coverage, 4),
        "mutualNeighborFraction": round(mutual_frac, 4),
        "jumpLengths": jumps,
        "continuationError": None if cont is None else {
            "mean": round(cont[0], 6),
            "median": round(cont[1], 6),
        },
    }

    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
