import os, re, math
import trimesh
import numpy as np

# Load manifest
import json
manifest = json.load(open('Assets/Artwork/SourceModels/PanzerIV/manifest.json', encoding='utf-8'))
nodes = {n['name']: n for n in manifest['nodes']}

# Load prefab
prefab_path = 'Assets/Resources/Artwork/Models/PanzerIV/PanzerIV.prefab'
with open(prefab_path, 'r', encoding='utf-8') as f:
    text = f.read()

doc_pattern = re.compile(r'--- !u!(\d+) &(\d+)\n(.*?)(?=\n--- !u!|\Z)', re.DOTALL)
docs = doc_pattern.findall(text)

game_objects = {}
transforms = {}
mesh_filters = {}

for class_id, file_id, body in docs:
    if class_id == '1':
        name_m = re.search(r'm_Name: ([^\n\r]+)', body)
        name = name_m.group(1) if name_m else "Unnamed"
        comp_m = re.findall(r'- component: \{fileID: (\d+)\}', body)
        trans_id = comp_m[0] if comp_m else None
        game_objects[file_id] = {'name': name, 'trans_id': trans_id}
    elif class_id == '4':
        father_m = re.search(r'm_Father: \{fileID: (\d+)\}', body)
        father_id = father_m.group(1) if father_m and father_m.group(1) != '0' else None
        children_m = re.findall(r'  - \{fileID: (\d+)\}', body)
        pos_m = re.search(r'm_LocalPosition: \{x: ([^,]+), y: ([^,]+), z: ([^\}]+)\}', body)
        pos = [float(pos_m.group(1)), float(pos_m.group(2)), float(pos_m.group(3))] if pos_m else [0,0,0]
        rot_m = re.search(r'm_LocalRotation: \{x: ([^,]+), y: ([^,]+), z: ([^,]+), w: ([^\}]+)\}', body)
        rot = [float(rot_m.group(1)), float(rot_m.group(2)), float(rot_m.group(3)), float(rot_m.group(4))] if rot_m else [0,0,0,1]
        scl_m = re.search(r'm_LocalScale: \{x: ([^,]+), y: ([^,]+), z: ([^\}]+)\}', body)
        scl = [float(scl_m.group(1)), float(scl_m.group(2)), float(scl_m.group(3))] if scl_m else [1,1,1]
        go_m = re.search(r'm_GameObject: \{fileID: (\d+)\}', body)
        go_id = go_m.group(1) if go_m else None
        transforms[file_id] = {
            'father_id': father_id,
            'children': children_m,
            'pos': pos,
            'rot': rot,
            'scl': scl,
            'go_id': go_id
        }
    elif class_id == '33': # MeshFilter
        go_m = re.search(r'm_GameObject: \{fileID: (\d+)\}', body)
        mesh_m = re.search(r'm_Mesh: \{fileID: [^,]+, guid: ([0-9a-fA-F]+)', body)
        if go_m and mesh_m:
            mesh_filters[go_m.group(1)] = mesh_m.group(1)

def get_path(tid):
    t = transforms[tid]
    go_name = game_objects.get(t['go_id'], {}).get('name', 'Unknown')
    if t['father_id'] is None or t['father_id'] not in transforms:
        return go_name
    return get_path(t['father_id']) + "/" + go_name

# Transform math in 4x4 matrix
def quat_to_mat(q):
    x, y, z, w = q
    return np.array([
        [1 - 2*y*y - 2*z*z, 2*x*y - 2*z*w,     2*x*z + 2*y*w,     0],
        [2*x*y + 2*z*w,     1 - 2*x*x - 2*z*z, 2*y*z - 2*x*w,     0],
        [2*x*z - 2*y*w,     2*y*z + 2*x*w,     1 - 2*x*x - 2*y*y, 0],
        [0, 0, 0, 1]
    ])

def local_matrix(t):
    R = quat_to_mat(t['rot'])
    S = np.diag([t['scl'][0], t['scl'][1], t['scl'][2], 1.0])
    T = np.eye(4)
    T[0, 3] = t['pos'][0]
    T[1, 3] = t['pos'][1]
    T[2, 3] = t['pos'][2]
    return T @ R @ S

def world_matrix(tid):
    t = transforms[tid]
    M = local_matrix(t)
    if t['father_id'] is not None and t['father_id'] in transforms:
        return world_matrix(t['father_id']) @ M
    return M

# Build meta guid map for meshes
guid_to_obj = {}
src_dir = 'Assets/Artwork/SourceModels/PanzerIV'
for fn in os.listdir(src_dir):
    if fn.endswith('.meta'):
        meta_txt = open(os.path.join(src_dir, fn), errors='ignore').read()
        gm = re.search(r'guid: ([0-9a-fA-F]+)', meta_txt)
        if gm:
            guid_to_obj[gm.group(1)] = fn[:-5]

cache_obj = {}

rows = []
for tid, t in transforms.items():
    go_id = t['go_id']
    go_name = game_objects.get(go_id, {}).get('name', '')
    path = get_path(tid)
    M = world_matrix(tid)
    
    # Calculate lossy scale
    lossy_scl = (np.linalg.norm(M[:3, 0]), np.linalg.norm(M[:3, 1]), np.linalg.norm(M[:3, 2]))
    w_pos = (M[0, 3], M[1, 3], M[2, 3])
    
    bounds_str = "None"
    mesh_fn = None
    if go_id in mesh_filters:
        guid = mesh_filters[go_id]
        mesh_fn = guid_to_obj.get(guid)
        if mesh_fn and os.path.exists(os.path.join(src_dir, mesh_fn)):
            if mesh_fn not in cache_obj:
                m_obj = trimesh.load(os.path.join(src_dir, mesh_fn))
                cache_obj[mesh_fn] = m_obj.vertices
            verts = cache_obj[mesh_fn]
            v_h = np.hstack([verts, np.ones((len(verts), 1))])
            v_w = (M @ v_h.T).T[:, :3]
            min_b = v_w.min(axis=0)
            max_b = v_w.max(axis=0)
            bounds_str = f"[{min_b[0]:.2f}..{max_b[0]:.2f}, {min_b[1]:.2f}..{max_b[1]:.2f}, {min_b[2]:.2f}..{max_b[2]:.2f}]"
            
    rows.append({
        'path': path,
        'name': go_name,
        'lpos': t['pos'],
        'lrot': t['rot'],
        'lscl': t['scl'],
        'lossy_scl': lossy_scl,
        'wpos': w_pos,
        'bounds': bounds_str,
        'mesh': mesh_fn
    })

rows.sort(key=lambda r: r['path'])

with open('scratch/prefab_diagnosis_table.txt', 'w') as out:
    out.write(f"{'Hierarchy Path':<55} | {'localPosition':<20} | {'localRotation':<25} | {'localScale':<18} | {'lossyScale':<18} | {'World Bounds'}\n")
    out.write("-" * 175 + "\n")
    for r in rows:
        lp = f"({r['lpos'][0]:.2f}, {r['lpos'][1]:.2f}, {r['lpos'][2]:.2f})"
        lr = f"({r['lrot'][0]:.2f}, {r['lrot'][1]:.2f}, {r['lrot'][2]:.2f}, {r['lrot'][3]:.2f})"
        ls = f"({r['lscl'][0]:.2f}, {r['lscl'][1]:.2f}, {r['lscl'][2]:.2f})"
        los = f"({r['lossy_scl'][0]:.2f}, {r['lossy_scl'][1]:.2f}, {r['lossy_scl'][2]:.2f})"
        out.write(f"{r['path']:<55} | {lp:<20} | {lr:<25} | {ls:<18} | {los:<18} | {r['bounds']}\n")

print(f"Wrote scratch/prefab_diagnosis_table.txt with {len(rows)} entries.")
