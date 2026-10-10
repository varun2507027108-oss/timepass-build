import os, re, math

prefab_path = 'Assets/Resources/Artwork/Models/PanzerIV/PanzerIV.prefab'

with open(prefab_path, 'r', encoding='utf-8') as f:
    text = f.read()

# Parse GameObjects
# --- !u!1 &<id>
# GameObject:
#   m_Name: <name>
#   m_Component:
#   - component: {fileID: <trans_id>}

doc_pattern = re.compile(r'--- !u!(\d+) &(\d+)\n(.*?)(?=\n--- !u!|\Z)', re.DOTALL)
docs = doc_pattern.findall(text)

game_objects = {} # id -> {name, transform_id}
transforms = {} # id -> {father_id, children_ids, pos, rot, scl, go_id}

for class_id, file_id, body in docs:
    if class_id == '1': # GameObject
        name_m = re.search(r'm_Name: ([^\n\r]+)', body)
        name = name_m.group(1) if name_m else "Unnamed"
        comp_m = re.findall(r'- component: \{fileID: (\d+)\}', body)
        # first component is usually transform
        trans_id = comp_m[0] if comp_m else None
        game_objects[file_id] = {'name': name, 'trans_id': trans_id}
    elif class_id == '4': # Transform
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

print(f"Parsed {len(game_objects)} GameObjects and {len(transforms)} Transforms.")

# Find root transform (father_id is None)
roots = [tid for tid, t in transforms.items() if t['father_id'] is None]
print(f"Root transforms: {len(roots)}")

# Build hierarchy paths and calculate world transforms
def get_path(tid):
    t = transforms[tid]
    go_name = game_objects.get(t['go_id'], {}).get('name', 'Unknown')
    if t['father_id'] is None or t['father_id'] not in transforms:
        return go_name
    return get_path(t['father_id']) + "/" + go_name

def mult_quat(q1, q2):
    x1, y1, z1, w1 = q1
    x2, y2, z2, w2 = q2
    return [
        w1*x2 + x1*w2 + y1*z2 - z1*y2,
        w1*y2 - x1*z2 + y1*w2 + z1*x2,
        w1*z2 + x1*y2 - y1*x2 + z1*w2,
        w1*w2 - x1*x2 - y1*y2 - z1*z2
    ]

def rot_vec(q, v):
    # v' = q * (v, 0) * q^-1
    x, y, z, w = q
    vx, vy, vz = v
    # t = 2 * cross(q.xyz, v)
    tx = 2 * (y * vz - z * vy)
    ty = 2 * (z * vx - x * vz)
    tz = 2 * (x * vy - y * vx)
    # v + w * t + cross(q.xyz, t)
    return [
        vx + w * tx + (y * tz - z * ty),
        vy + w * ty + (z * tx - x * tz),
        vz + w * tz + (x * ty - y * tx)
    ]

def get_world_transform(tid):
    t = transforms[tid]
    if t['father_id'] is None or t['father_id'] not in transforms:
        return t['pos'], t['rot'], t['scl']
    p_pos, p_rot, p_scl = get_world_transform(t['father_id'])
    
    # world_scale approx componentwise
    w_scl = [p_scl[0] * t['scl'][0], p_scl[1] * t['scl'][1], p_scl[2] * t['scl'][2]]
    # scaled local pos
    scaled_lpos = [t['pos'][0] * p_scl[0], t['pos'][1] * p_scl[1], t['pos'][2] * p_scl[2]]
    # rotated by parent rot
    rot_lpos = rot_vec(p_rot, scaled_lpos)
    w_pos = [p_pos[0] + rot_lpos[0], p_pos[1] + rot_lpos[1], p_pos[2] + rot_lpos[2]]
    w_rot = mult_quat(p_rot, t['rot'])
    return w_pos, w_rot, w_scl

entries = []
for tid, t in transforms.items():
    go_name = game_objects.get(t['go_id'], {}).get('name', 'Unknown')
    path = get_path(tid)
    w_pos, w_rot, w_scl = get_world_transform(tid)
    entries.append({
        'path': path,
        'name': go_name,
        'lpos': t['pos'],
        'lrot': t['rot'],
        'lscl': t['scl'],
        'wpos': w_pos,
        'wscl': w_scl
    })

entries.sort(key=lambda x: x['path'])

print("\n--- TRANSFORMS TABLE ---")
print(f"{'Path':<55} | {'localPosition':<22} | {'localScale':<20} | {'worldPos':<22} | {'lossyScale':<18}")
print("-" * 145)
for e in entries:
    lp_str = f"({e['lpos'][0]:.2f}, {e['lpos'][1]:.2f}, {e['lpos'][2]:.2f})"
    ls_str = f"({e['lscl'][0]:.2f}, {e['lscl'][1]:.2f}, {e['lscl'][2]:.2f})"
    wp_str = f"({e['wpos'][0]:.2f}, {e['wpos'][1]:.2f}, {e['wpos'][2]:.2f})"
    ws_str = f"({e['wscl'][0]:.2f}, {e['wscl'][1]:.2f}, {e['wscl'][2]:.2f})"
    print(f"{e['path']:<55} | {lp_str:<22} | {ls_str:<20} | {wp_str:<22} | {ws_str:<18}")
