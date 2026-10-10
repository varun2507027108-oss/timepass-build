import json, os

manifest_path = "Assets/Artwork/SourceModels/PanzerIV/manifest.json"
with open(manifest_path, "r") as f:
    data = json.load(f)

nodes = data.get("nodes", [])
print(f"Total manifest nodes: {len(nodes)}")

# Let's inspect meshes and positions
for n in nodes:
    name = n.get("name", "")
    mesh = n.get("mesh", "")
    pos = n.get("pos", [])
    scl = n.get("scl", [])
    if any(k in name.lower() for k in ["wheel", "track", "fender", "roller", "sprocket", "idler", "hull"]):
        print(f"{name:<30} mesh={mesh:<30} pos={pos} scl={scl}")
