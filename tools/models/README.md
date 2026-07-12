# BeatThis ONNX models (optional)

Place a BeatThis ONNX file here so `analyze_track.py` can use neural beat/downbeat
tracking without the full PyTorch `beat-this` package.

## Recommended (small, ~10 MB)

Download `small0.onnx` from:
https://huggingface.co/ashudesai/songbird-models

Save as:

```
tools/models/beat_this_small0.onnx
```

## Or install the official package

```
pip install beat-this
```

That uses the CPJKU checkpoints directly (larger download, most accurate).

## Fallback

If neither is available, analysis uses librosa Ellis DP beat tracking and marks
beats with lower confidence. Classic feature vectors still ship either way.
