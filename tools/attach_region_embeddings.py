#!/usr/bin/env python3
"""Slice 5 helper: attach region embeddings to an analysis JSON (sidecar merge).

Does NOT require Essentia at import time. Two modes:

1) --from-npy path.npy
   Load an (N, D) float array (one row per beat) and write regionEmbeddings into the analysis.

2) --essentia (optional)
   If `essentia` + a TensorFlow model are installed, extract beat-pooled embeddings
   (msd-musicnn / discogs-effnet style). Falls back with a clear error if unavailable.

Usage:
    python tools/attach_region_embeddings.py analysis.json --from-npy embeds.npy [--inplace]
    python tools/attach_region_embeddings.py analysis.json --essentia [--model musicnn]

After merge, rebuild the jukebox graph in-app (toggle Essentia region gate / Invalidate).
"""

from __future__ import print_function

import argparse
import json
import os
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("analysis_json")
    parser.add_argument("--from-npy", help="Path to (N,D) float32/float64 npy")
    parser.add_argument("--essentia", action="store_true",
                        help="Try Essentia TensorFlow embedding extraction")
    parser.add_argument("--model", default="musicnn",
                        help="musicnn | effnet (essentia mode)")
    parser.add_argument("--wav", help="WAV path for essentia mode")
    parser.add_argument("--inplace", action="store_true")
    parser.add_argument("-o", "--output", help="Output analysis JSON path")
    args = parser.parse_args()

    with open(args.analysis_json) as handle:
        analysis = json.load(handle)

    beats = analysis.get("beats") or []
    n = len(beats)

    if n == 0:
        sys.stderr.write("Analysis has no beats.\n")
        sys.exit(3)

    embeds = None

    if args.from_npy:
        try:
            import numpy as np
        except ImportError:
            sys.stderr.write("numpy required for --from-npy\n")
            sys.exit(2)

        arr = np.load(args.from_npy)
        if arr.ndim != 2 or arr.shape[0] != n:
            sys.stderr.write(
                "Expected npy shape (%d, D); got %s\n" % (n, getattr(arr, "shape", None)))
            sys.exit(4)
        embeds = arr.astype(float).tolist()
    elif args.essentia:
        embeds = _extract_essentia(args.wav, beats, args.model)
    else:
        sys.stderr.write("Provide --from-npy or --essentia.\n")
        sys.exit(2)

    analysis["regionEmbeddings"] = embeds
    analysis["regionEmbeddingModel"] = args.model if args.essentia else "npy"

    out = args.output
    if args.inplace:
        out = args.analysis_json
    if not out:
        root, ext = os.path.splitext(args.analysis_json)
        out = root + ".regions" + (ext or ".json")

    with open(out, "w") as handle:
        json.dump(analysis, handle)
    sys.stderr.write("Wrote %d region embeddings → %s\n" % (len(embeds), out))


def _extract_essentia(wav_path, beats, model_name):
    if not wav_path or not os.path.isfile(wav_path):
        sys.stderr.write("--essentia requires --wav path to the track WAV.\n")
        sys.exit(5)

    try:
        import numpy as np
        # Lazy: only import if user asked; many machines lack Essentia TF.
        import essentia.standard as es  # type: ignore
    except Exception as ex:
        sys.stderr.write(
            "Essentia TensorFlow not available (%s).\n"
            "Install essentia-tensorflow or pass --from-npy instead.\n" % ex)
        sys.exit(6)

    # Placeholder pooling: mean of consecutive embedding frames mapped onto beat grid.
    # Real musicnn/effnet wiring depends on local model files; keep this path explicit.
    sys.stderr.write(
        "Essentia is importable, but model file wiring is environment-specific.\n"
        "Prefer exporting embeddings yourself and using --from-npy until models are bundled.\n")
    sys.exit(7)


if __name__ == "__main__":
    main()
