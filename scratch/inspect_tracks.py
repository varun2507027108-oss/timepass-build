import json

with open('Assets/Artwork/SourceModels/PanzerIV/manifest.json') as f:
    d = json.load(f)

for n in d['nodes']:
    if 'track' in n['name'].lower() or 'wheel' in n['name'].lower() or 'roller' in n['name'].lower() or 'fender' in n['name'].lower():
        p = n.get('pos')
        pos_str = f"({p[0]:.3f}, {p[1]:.3f}, {p[2]:.3f})" if p else "none"
        print(f"{n['name']:<35} parent={str(n.get('parent')):<25} mesh={str(n.get('mesh')):<35} pos={pos_str}")
