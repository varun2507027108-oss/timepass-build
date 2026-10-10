import os

for fname in ['track_001_low_track_0.obj', 'track_001_low_track_1.obj', 'track_001_low_track_0_2.obj']:
    path = os.path.join('Assets/Artwork/SourceModels/PanzerIV', fname)
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        lines = f.readlines()
    v_count = sum(1 for l in lines if l.startswith('v '))
    f_count = sum(1 for l in lines if l.startswith('f '))
    print(f"{fname}: {v_count} vertices, {f_count} faces")
