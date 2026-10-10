import re

prefab_path = 'Assets/Resources/Artwork/Models/PanzerIV/PanzerIV.prefab'
with open(prefab_path, 'r', encoding='utf-8') as f:
    text = f.read()

doc_pattern = re.compile(r'--- !u!(\d+) &(\d+)\n(.*?)(?=\n--- !u!|\Z)', re.DOTALL)
docs = doc_pattern.findall(text)

game_objects = {}
transforms = {}

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

def get_path(tid):
    t = transforms[tid]
    go_name = game_objects.get(t['go_id'], {}).get('name', 'Unknown')
    if t['father_id'] is None or t['father_id'] not in transforms:
        return go_name
    return get_path(t['father_id']) + "/" + go_name

print("DIRECT CHILDREN OF RootNode:")
root_node_tids = [tid for tid, t in transforms.items() if game_objects.get(t['go_id'], {}).get('name') == 'RootNode']
print("RootNode IDs:", root_node_tids)

if root_node_tids:
    rn_tid = root_node_tids[0]
    children = transforms[rn_tid]['children']
    print(f"Total direct children of RootNode: {len(children)}")
    for ctid in children:
        c = transforms[ctid]
        cname = game_objects.get(c['go_id'], {}).get('name')
        print(f"  {cname:<25} localPos=({c['pos'][0]:6.2f}, {c['pos'][1]:6.2f}, {c['pos'][2]:6.2f}) localScale=({c['scl'][0]:.2f}, {c['scl'][1]:.2f}, {c['scl'][2]:.2f})")
