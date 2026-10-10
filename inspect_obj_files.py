import trimesh
import numpy as np

# Load the OBJ mesh directly
obj_mesh = trimesh.load('Assets/Artwork/SourceModels/PanzerIV/fender_low_hull_001_0.obj')
print("OBJ bounds:", obj_mesh.bounds)
print("OBJ center:", (obj_mesh.bounds[0] + obj_mesh.bounds[1]) / 2)
print("OBJ size:", obj_mesh.bounds[1] - obj_mesh.bounds[0])

# Also check track_001_low_track_0.obj
track_mesh = trimesh.load('Assets/Artwork/SourceModels/PanzerIV/track_001_low_track_0.obj')
print("\nTrack OBJ bounds:", track_mesh.bounds)
print("Track OBJ center:", (track_mesh.bounds[0] + track_mesh.bounds[1]) / 2)
print("Track OBJ size:", track_mesh.bounds[1] - track_mesh.bounds[0])

# Also check wheel_001_low_drivetrain_0.obj
wheel_mesh = trimesh.load('Assets/Artwork/SourceModels/PanzerIV/wheel_001_low_drivetrain_0.obj')
print("\nWheel OBJ bounds:", wheel_mesh.bounds)
print("Wheel OBJ center:", (wheel_mesh.bounds[0] + wheel_mesh.bounds[1]) / 2)
print("Wheel OBJ size:", wheel_mesh.bounds[1] - wheel_mesh.bounds[0])
