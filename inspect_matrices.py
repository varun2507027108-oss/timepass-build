import trimesh
import numpy as np

scene = trimesh.load('Assets/Resources/Artwork/Models/PanzerIV/panzer_iv_low.glb')

print("Root node:", scene.graph.base_frame)
for node in ['wheel_001_low', 'roller_001_low', 'fender_low', 'hull_low', 'turret_low', 'track_001_low', 'suspension_low', 'driveSprocket_001_low', 'rearIdler_low']:
    matrix, geom = scene.graph.get(node)
    print(f"\nNode {node} -> geom={geom}")
    print("Matrix in world:\n", np.round(matrix, 3))
