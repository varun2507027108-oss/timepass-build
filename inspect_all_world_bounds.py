import trimesh
import numpy as np

scene = trimesh.load('Assets/Resources/Artwork/Models/PanzerIV/panzer_iv_low.glb')

print("All geometries transformed into world:")
for node in scene.graph.nodes:
    matrix, geom_name = scene.graph.get(node)
    if geom_name and geom_name in scene.geometry:
        geom = scene.geometry[geom_name]
        # transform vertices by matrix
        v = geom.vertices
        v_h = np.hstack([v, np.ones((len(v), 1))])
        v_w = (matrix @ v_h.T).T[:, :3]
        min_w = v_w.min(axis=0)
        max_w = v_w.max(axis=0)
        center_w = (min_w + max_w) / 2
        size_w = max_w - min_w
        print(f"{node:<35} | {geom_name:<35} | min=({min_w[0]:6.2f},{min_w[1]:6.2f},{min_w[2]:6.2f}) max=({max_w[0]:6.2f},{max_w[1]:6.2f},{max_w[2]:6.2f}) center=({center_w[0]:6.2f},{center_w[1]:6.2f},{center_w[2]:6.2f})")
