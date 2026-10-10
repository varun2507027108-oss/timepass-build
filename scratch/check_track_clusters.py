import os
import numpy as np

for fname in ['track_001_low_track_0.obj', 'track_001_low_track_1.obj', 'track_001_low_track_0_2.obj']:
    path = os.path.join('Assets/Artwork/SourceModels/PanzerIV', fname)
    xs = []
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            if line.startswith('v '):
                parts = line.strip().split()
                xs.append(float(parts[1]))
    xs = np.array(xs)
    print(f"\n--- {fname} ---")
    print(f"Total verts: {len(xs)}")
    print(f"X min: {xs.min():.3f}, max: {xs.max():.3f}")
    left = xs[xs < 0]
    right = xs[xs > 0]
    print(f"Left (x < 0): count={len(left)}, range=[{left.min():.3f}, {left.max():.3f}]" if len(left) else "Left: None")
    print(f"Right (x > 0): count={len(right)}, range=[{right.min():.3f}, {right.max():.3f}]" if len(right) else "Right: None")
