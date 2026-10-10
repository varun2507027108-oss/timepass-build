import json
import trimesh
import numpy as np

m = json.load(open('Assets/Artwork/SourceModels/PanzerIV/manifest.json'))
print("Manifest source:", m.get('source'))

scene = trimesh.load('Assets/Resources/Artwork/Models/PanzerIV/panzer_iv_low.glb')

nodes_to_check = ['fender_low', 'wheel_001_low', 'track_001_low', 'turret_low', 'RootNode', 'Sketchfab_model']
manifest_dict = {n['name']: n for n in m['nodes']}

for name in nodes_to_check:
    print(f"\n--- {name} ---")
    if name in manifest_dict:
        mn = manifest_dict[name]
        print("Manifest parent:", mn.get('parent'))
        print("Manifest pos:", mn.get('pos'))
        print("Manifest rot:", mn.get('rot'))
        print("Manifest scl:", mn.get('scl'))
    
    parents = scene.graph.transforms.parents.get(name)
    print("GLB parent:", parents)
    if parents:
        mat, _ = scene.graph.get(frame_to=name, frame_from=parents)
        print("GLB local matrix to parent:\n", np.round(mat, 3))
