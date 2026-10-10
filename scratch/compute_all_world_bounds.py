import re
import os
import numpy as np

# Load prefab
with open('Assets/Resources/Artwork/Models/PanzerIV/PanzerIV.prefab', 'r', encoding='utf-8', errors='ignore') as f:
    text = f.read()

entries = text.split('--- !u!')
gos = {}
trs = {}
mfs = {}

# Parse
for e in entries:
    lines = e.splitlines()
    if not lines: continue
    h = lines[0].strip()
    if h.startswith('1 &'):
        fid = h.split('&')[1].strip()
        m = re.search(r'm_Name:\s*(.+)', e)
        if m: gos[fid] = m.group(1).strip()
    elif h.startswith('4 &'):
        fid = h.split('&')[1].strip()
        go_m = re.search(r'm_GameObject:\s*\{fileID:\s*(\d+)\}', e)
        pos_m = re.search(r'm_LocalPosition:\s*\{x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^}]+)\}', e)
        rot_m = re.search(r'm_LocalRotation:\s*\{x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^,]+),\s*w:\s*([^}]+)\}', e)
        scl_m = re.search(r'm_LocalScale:\s*\{x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^}]+)\}', e)
        fat_m = re.search(r'm_Father:\s*\{fileID:\s*(\d+)\}', e)
        trs[fid] = {
            'go_id': go_m.group(1) if go_m else None,
            'pos': np.array([float(pos_m.group(1)), float(pos_m.group(2)), float(pos_m.group(3))]) if pos_m else np.zeros(3),
            'rot': np.array([float(rot_m.group(1)), float(rot_m.group(2)), float(rot_m.group(3)), float(rot_m.group(4))]) if rot_m else np.array([0,0,0,1]),
            'scl': np.array([float(scl_m.group(1)), float(scl_m.group(2)), float(scl_m.group(3))]) if scl_m else np.ones(3),
            'father': fat_m.group(1) if fat_m else None
        }
    elif h.startswith('33 &'):
        fid = h.split('&')[1].strip()
        go_m = re.search(r'm_GameObject:\s*\{fileID:\s*(\d+)\}', e)
        mesh_m = re.search(r'm_Mesh:\s*\{fileID:\s*([^,]+),\s*guid:\s*([^,]+)', e)
        if go_m and mesh_m:
            mfs[go_m.group(1)] = mesh_m.group(2).strip()

# Map guid to obj filename from meta files
guid_to_obj = {}
for root, dirs, files in os.walk('Assets/Artwork/SourceModels/PanzerIV'):
    for file in files:
        if file.endswith('.obj.meta'):
            with open(os.path.join(root, file)) as mf:
                content = mf.read()
                gm = re.search(r'guid:\s*([a-f0-9]+)', content)
                if gm:
                    guid_to_obj[gm.group(1)] = file[:-5]

# Function to get quat matrix
def quat_to_mat(q):
    x, y, z, w = q
    return np.array([
        [1 - 2*y*y - 2*z*z, 2*x*y - 2*z*w,     2*x*z + 2*y*w,     0],
        [2*x*y + 2*z*w,     1 - 2*x*x - 2*z*z, 2*y*z - 2*x*w,     0],
        [2*x*z - 2*y*w,     2*y*z + 2*x*w,     1 - 2*x*x - 2*y*y, 0],
        [0, 0, 0, 1]
    ])

def get_local_mat(t):
    R = quat_to_mat(t['rot'])
    S = np.diag([t['scl'][0], t['scl'][1], t['scl'][2], 1.0])
    T = np.eye(4)
    T[:3, 3] = t['pos']
    return T @ R @ S

# Compute world matrices
world_mats = {}
def get_world_mat(tfid):
    if tfid in world_mats: return world_mats[tfid]
    t = trs[tfid]
    L = get_local_mat(t)
    if t['father'] and t['father'] in trs:
        W = get_world_mat(t['father']) @ L
    else:
        W = L
    world_mats[tfid] = W
    return W

# For each renderer, compute world bounds
results = []
for tfid, t in trs.items():
    goid = t['go_id']
    gname = gos.get(goid, 'unknown')
    if goid in mfs:
        guid = mfs[goid]
        obj_name = guid_to_obj.get(guid, 'unknown.obj')
        # Load obj vertices
        obj_path = os.path.join('Assets/Artwork/SourceModels/PanzerIV', obj_name)
        if os.path.exists(obj_path):
            verts = []
            with open(obj_path, 'r', encoding='utf-8', errors='ignore') as f:
                for line in f:
                    if line.startswith('v '):
                        parts = line.strip().split()
                        verts.append([float(parts[1]), float(parts[2]), float(parts[3])])
            if verts:
                verts = np.array(verts)
                # Apply world mat
                W = get_world_mat(tfid)
                verts_h = np.hstack([verts, np.ones((len(verts), 1))])
                w_verts = (verts_h @ W.T)[:, :3]
                min_b = w_verts.min(axis=0)
                max_b = w_verts.max(axis=0)
                center = (min_b + max_b) / 2.0
                results.append((gname, obj_name, min_b, max_b, center))

results.sort(key=lambda x: x[4][0]) # sort by center X
print(f"{'GameObject':<32} {'Mesh OBJ':<32} {'X Range':<18} {'Y Range':<18} {'Z Range':<18} {'Center X':<8}")
for r in results:
    gname, oname, min_b, max_b, c = r
    xr = f"[{min_b[0]:5.2f}, {max_b[0]:5.2f}]"
    yr = f"[{min_b[1]:5.2f}, {max_b[1]:5.2f}]"
    zr = f"[{min_b[2]:5.2f}, {max_b[2]:5.2f}]"
    print(f"{gname:<32} {oname:<32} {xr:<18} {yr:<18} {zr:<18} {c[0]:6.2f}")
