#!/usr/bin/env python3
"""Slice 2/4 harness — Classic graph quality + mutual-kNN / component metrics.

Usage:
    python tools/jukebox_harness.py path/to/analysis.json [--k 6] [--percentile 55] [--min-jump 8] [--mutual]

Classic edges are continuation-oriented: i→j scores euclid(stack[i+1], stack[j]).

Reports: dead-beat rate, jump-length dist, continuation error, mutual coverage,
component count / fragmentation, optional DP↔BeatThis F-measure from analysis.dpAgreement.
"""

from __future__ import print_function

import argparse
import json
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


def build_knn(vectors, beats, k=6, percentile=55.0, min_jump=8):
    """Directed continuation kNN: edge i→j scores euclid(stack[i+1], stack[j])."""
    n = len(vectors)
    pairs = []
    dist = np.full((n, n), np.inf)
    for i in range(n - 1):
        nxt = vectors[i + 1]
        for j in range(n):
            if j == i or abs(i - j) < min_jump:
                continue
            d = euclid(nxt, vectors[j])
            dist[i, j] = d
            pairs.append(d)

    if not pairs:
        return [[] for _ in range(n)], 0.0, dist

    thr = float(np.percentile(pairs, percentile))
    neighbors = []
    for i in range(n):
        cands = [(j, dist[i, j]) for j in range(n)
                 if abs(i - j) >= min_jump and dist[i, j] <= thr]
        cands.sort(key=lambda x: x[1])
        neighbors.append(cands[:k])
    return neighbors, thr, dist


def apply_mutual(neighbors):
    n = len(neighbors)
    dest_sets = [{j for j, _ in edges} for edges in neighbors]
    mutual = []
    for i in range(n):
        kept = [(j, d) for j, d in neighbors[i] if i in dest_sets[j]]
        mutual.append(kept)
    return mutual


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
    return edged / float(n), (mutual / 2.0) / float(max(1, edged))


def connected_components(neighbors):
    """Undirected components over neighbor lists."""
    n = len(neighbors)
    undirected = [set() for _ in range(n)]
    for i, edges in enumerate(neighbors):
        for j, _ in edges:
            undirected[i].add(j)
            undirected[j].add(i)

    ids = [-1] * n
    component = 0
    sizes = []
    for i in range(n):
        if ids[i] >= 0:
            continue
        stack = [i]
        ids[i] = component
        size = 0
        while stack:
            u = stack.pop()
            size += 1
            for v in undirected[u]:
                if ids[v] < 0:
                    ids[v] = component
                    stack.append(v)
        sizes.append(size)
        component += 1

    orphans = sum(1 for edges in neighbors if not edges)
    singleton_components = sum(1 for s in sizes if s == 1)
    return {
        "componentCount": component,
        "largestComponent": int(max(sizes) if sizes else 0),
        "singletonComponents": singleton_components,
        "orphanBeats": orphans,
        "fragmentation": round(component / float(max(1, n)), 4),
    }


def continuation_error(vectors, neighbors):
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
    parser.add_argument("--mutual", action="store_true",
                        help="Filter to mutual-kNN before metrics (Slice 4 topology)")
    args = parser.parse_args()

    with open(args.analysis_json) as handle:
        analysis = json.load(handle)

    vectors, beats = load_vectors(analysis)
    neighbors, thr, _dist = build_knn(vectors, beats, args.k, args.percentile, args.min_jump)
    directed = neighbors
    if args.mutual:
        neighbors = apply_mutual(neighbors)

    n = len(neighbors)
    dead = sum(1 for edges in neighbors if not edges)
    coverage, mutual_frac = mutual_coverage(directed)
    cont = continuation_error(vectors, neighbors)
    jumps = jump_length_stats(neighbors)
    components = connected_components(neighbors)

    report = {
        "trackId": analysis.get("trackId"),
        "beatTracker": analysis.get("beatTracker"),
        "beats": n,
        "gapSplitInserts": analysis.get("gapSplitInserts", 0),
        "dpAgreement": analysis.get("dpAgreement"),
        "qualityPercentile": args.percentile,
        "mutualKnnApplied": bool(args.mutual),
        "distanceThreshold": round(thr, 6),
        "deadBeatRate": round(dead / float(n), 4),
        "deadBeats": dead,
        "branchableCoverage": round(coverage if not args.mutual else
                                    sum(1 for e in neighbors if e) / float(n), 4),
        "mutualNeighborFraction": round(mutual_frac, 4),
        "components": components,
        "jumpLengths": jumps,
        "continuationError": None if cont is None else {
            "mean": round(cont[0], 6),
            "median": round(cont[1], 6),
        },
        "hasRegionEmbeddings": bool(analysis.get("regionEmbeddings")),
    }

    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
