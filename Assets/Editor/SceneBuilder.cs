using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WarArena.Gameplay;

namespace WarArena.EditorTools
{
    /// <summary>
    /// Generates the playable battlefield scene as a real Unity asset.
    ///
    /// Scenes are built programmatically rather than hand-authored as YAML, because
    /// hand-written scene files depend on stable .meta GUIDs and fileIDs and corrupt
    /// the moment a reference is reordered. The scene holds exactly one GameObject,
    /// the bootstrap, so there are no serialized references that can break.
    /// </summary>
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Kursk.unity";

        [MenuItem("WarArena/Build Playable Scene")]
        public static void BuildScene()
        {
            Directory.CreateDirectory(Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? ".", "Assets/Scenes"));

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var bootstrap = new GameObject("WarArenaBootstrap");
            bootstrap.AddComponent<WarArenaBootstrap>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[WarArena] Scene written to " + ScenePath);
        }

        /// <summary>
        /// Headless entry point:
        ///   Unity.exe -batchmode -quit -executeMethod WarArena.EditorTools.SceneBuilder.BuildScene
        /// </summary>
        public static void BuildSceneBatch()
        {
            BuildScene();
        }
    }
}