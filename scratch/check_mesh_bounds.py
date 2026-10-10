import os

def get_obj_bounds(path):
    min_x = min_y = min_z = float('inf')
    max_x = max_y = max_z = float('-inf')
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            if line.startswith('v '):
                parts = line.strip().split()
                x, y, z = float(parts[1]), float(parts[2]), float(parts[3])
                min_x = min(min_x, x); max_x = max(max_x, x)
                min_y = min(min_y, y); max_y = max(max_y, y)
                min_z = min(min_z, z); max_z = max(max_z, z)
    return (min_x, max_x), (min_y, max_y), (min_z, max_z)

for fname in [
    'track_001_low_track_0.obj',
    'track_001_low_track_1.obj',
    'track_001_low_track_0_2.obj',
    'track_002_low_track_0.obj',
    'track_003_low_track_0.obj',
    'wheel_001_low_drivetrain_0.obj',
    'roller_001_low_drivetrain_0.obj',
    'fender_low_hull_001_0.obj',
    'hull_low_hull_002_0.obj',
    'turret_low_turret_0.obj'
]:
    p = os.path.join('Assets/Artwork/SourceModels/PanzerIV', fname)
    if os.path.exists(p):
        xb, yb, zb = get_obj_bounds(p)
        print(f"{fname:<32} X: [{xb[0]:6.2f}, {xb[1]:6.2f}]  Y: [{yb[0]:6.2f}, {yb[1]:6.2f}]  Z: [{zb[0]:6.2f}, {zb[1]:6.2f}]")
