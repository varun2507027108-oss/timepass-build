import re

with open('Assets/Resources/Artwork/Models/PanzerIV/PanzerIV.prefab', 'r', encoding='utf-8', errors='ignore') as f:
    text = f.read()

entries = text.split('--- !u!')
gos = {}
trs = {}

for e in entries:
    lines = e.splitlines()
    if not lines: continue
    header = lines[0].strip()
    # e.g. "1 &427550531989840587"
    if header.startswith('1 &'):
        fid = header.split('&')[1].strip()
        name_m = re.search(r'm_Name:\s*(.+)', e)
        if name_m:
            gos[fid] = name_m.group(1)
    elif header.startswith('4 &'):
        fid = header.split('&')[1].strip()
        go_m = re.search(r'm_GameObject:\s*\{fileID:\s*(\d+)\}', e)
        pos_m = re.search(r'm_LocalPosition:\s*\{x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^}]+)\}', e)
        rot_m = re.search(r'm_LocalRotation:\s*\{x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^,]+),\s*w:\s*([^}]+)\}', e)
        scl_m = re.search(r'm_LocalScale:\s*\{x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^}]+)\}', e)
        father_m = re.search(r'm_Father:\s*\{fileID:\s*(\d+)\}', e)
        
        trs[fid] = {
            'go_id': go_m.group(1) if go_m else None,
            'pos': [float(pos_m.group(1)), float(pos_m.group(2)), float(pos_m.group(3))] if pos_m else [0,0,0],
            'rot': [float(rot_m.group(1)), float(rot_m.group(2)), float(rot_m.group(3)), float(rot_m.group(4))] if rot_m else [0,0,0,1],
            'scl': [float(scl_m.group(1)), float(scl_m.group(2)), float(scl_m.group(3))] if scl_m else [1,1,1],
            'father': father_m.group(1) if father_m else None
        }

print(f"Total GameObjects: {len(gos)}, Total Transforms: {len(trs)}")

tr_names = {}
for tfid, t in trs.items():
    gname = gos.get(t['go_id'], 'unknown')
    tr_names[tfid] = gname

for tfid, t in trs.items():
    name = tr_names[tfid]
    father_name = tr_names.get(t['father'], 'ROOT')
    p = t['pos']
    if any(k in name.lower() for k in ['track', 'wheel', 'roller', 'fender', 'turret', 'gun', 'rootnode', 'panzeriv', 'sketchfab']):
        print(f"{name:<35} father={father_name:<30} pos=({p[0]:6.3f}, {p[1]:6.3f}, {p[2]:6.3f}) scl={t['scl']}")
