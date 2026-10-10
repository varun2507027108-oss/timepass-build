import os

def get_obj_bounds(path):
    xs, ys, zs = [], [], []
    with open(path, 'r', errors='ignore') as f:
        for line in f:
            if line.startswith('v '):
                parts = line.strip().split()
                xs.append(float(parts[1]))
                ys.append(float(parts[2]))
                zs.append(float(parts[3]))
    if not xs: return None
    return (min(xs), max(xs)), (min(ys), max(ys)), (min(zs), max(zs))

source_dir = 'Assets/Artwork/SourceModels/PanzerIV'
for fn in sorted(os.listdir(source_dir)):
    if fn.endswith('.obj') and any(k in fn for k in ['track', 'wheel', 'fender', 'hull', 'suspension', 'roller', 'sprocket', 'idler']):
        bounds = get_obj_bounds(os.path.join(source_dir, fn))
        if bounds:
            (minx, maxx), (miny, maxy), (minz, maxz) = bounds
            print(f"{fn:<40} X:[{minx:6.2f}, {maxx:6.2f}] Y:[{miny:6.2f}, {maxy:6.2f}] Z:[{minz:6.2f}, {maxz:6.2f}]")
