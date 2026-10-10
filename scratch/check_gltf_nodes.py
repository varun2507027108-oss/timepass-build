import json

manifest = json.load(open('Assets/Artwork/SourceModels/PanzerIV/manifest.json'))

for n in manifest['nodes']:
    if n['name'] in ['Sketchfab_model', 'd5a06183065c4b249c165b69eac1cdd3.fbx', 'RootNode', 'fender_low', 'wheel_001_low', 'track_001_low']:
        print(f"Name: {n['name']}")
        print(f"  Parent: {n['parent']}")
        print(f"  Pos: {n['pos']}")
        print(f"  Rot: {n['rot']}")
        print(f"  Scl: {n['scl']}")
