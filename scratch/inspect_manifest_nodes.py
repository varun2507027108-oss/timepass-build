import json

with open('Assets/Artwork/SourceModels/PanzerIV/manifest.json') as f:
    d = json.load(f)

print('Total nodes:', len(d['nodes']))
for n in d['nodes']:
    name = n['name'].lower()
    if any(k in name for k in ['track', 'wheel', 'roller', 'fender', 'hull', 'turret', 'gun', 'rootnode', 'sketchfab']):
        p = n.get('pos')
        pos_str = f"({p[0]:.2f}, {p[1]:.2f}, {p[2]:.2f})" if p else "none"
        s = n.get('scl')
        scl_str = f"({s[0]:.2f}, {s[1]:.2f}, {s[2]:.2f})" if s else "none"
        print(f"{n['name']:<25} parent={str(n.get('parent')):<25} mesh={str(n.get('mesh')):<28} pos={pos_str} scl={scl_str}")
