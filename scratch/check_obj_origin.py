import trimesh
import numpy as np

scene = trimesh.load('Assets/Resources/Artwork/Models/PanzerIV/panzer_iv_low.glb')
obj = trimesh.load('Assets/Artwork/SourceModels/PanzerIV/wheel_001_low_drivetrain_0.obj')

geom = scene.geometry.get('wheel_001_low_drivetrain_0')
print("In GLB geom raw vertices bounds:")
print("min:", geom.vertices.min(axis=0))
print("max:", geom.vertices.max(axis=0))

print("\nIn OBJ vertices bounds:")
print("min:", obj.vertices.min(axis=0))
print("max:", obj.vertices.max(axis=0))

# Now let's see how node wheel_001_low transforms it
node_to_world, _ = scene.graph.get('wheel_001_low')
print("\nwheel_001_low node matrix:")
print(node_to_world)

# Let's see node wheel_001_low_drivetrain_0 in GLB:
matrix_geom, _ = scene.graph.get('wheel_001_low_drivetrain_0')
print("\nwheel_001_low_drivetrain_0 matrix to world:")
print(matrix_geom)

v_transformed = (matrix_geom @ np.hstack([geom.vertices, np.ones((len(geom.vertices), 1))]).T).T[:, :3]
print("\nTransformed to world bounds:")
print("min:", v_transformed.min(axis=0))
print("max:", v_transformed.max(axis=0))
