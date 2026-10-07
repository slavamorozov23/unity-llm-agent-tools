using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge.Editor
{
    [InitializeOnLoad]
    internal static class ScenePersistenceService
    {
        private static readonly MethodInfo ReloadScene = typeof(EditorSceneManager).GetMethod(
            "ReloadScene",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(Scene) },
            null);
        private static readonly MethodInfo ClearChangedOnDisk = typeof(EditorSceneManager).GetMethod(
            "ClearOpenScenesChangedOnDisk",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        private static readonly Dictionary<string, double> UnitySaves = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, FileStamp> FileStamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PendingReloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const string FingerprintKeyPrefix = "UnityAgentBridge.SceneFingerprint.";
        private const double DiskCheckInterval = 0.25d;
        private static bool saveScheduled;
        private static bool saving;
        private static double nextDiskCheck;

        static ScenePersistenceService()
        {
            if (ReloadScene == null)
                throw new MissingMethodException(typeof(EditorSceneManager).FullName, "ReloadScene");
            if (ClearChangedOnDisk == null)
                throw new MissingMethodException(typeof(EditorSceneManager).FullName, "ClearOpenScenesChangedOnDisk");
            EditorSceneManager.sceneDirtied += OnSceneDirtied;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += CheckOpenScenesOnDisk;
            for (var index = 0; index < SceneManager.sceneCount; index++)
                RememberCurrentFileIfUnknown(SceneManager.GetSceneAt(index));
        }

        // Unity refuses to mark a scene dirty in Play Mode, where edits are temporary; the edit itself still applies.
        public static void MarkDirty(Scene scene)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode && scene.IsValid())
                EditorSceneManager.MarkSceneDirty(scene);
        }

        public static void SaveBeforeTransition()
        {
            CancelScheduledSave();
            SaveCurrentPrefabStageNow();
            SaveOpenSceneChangesNow();
        }

        public static void SaveOpenSceneChangesNow()
        {
            SaveOpenSceneChangesNow(true);
        }

        private static void SaveOpenSceneChangesNow(bool requireSavedPath)
        {
            if (saving || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            saving = true;
            try
            {
                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                for (var index = 0; index < SceneManager.sceneCount; index++)
                {
                    var scene = SceneManager.GetSceneAt(index);
                    if (!scene.isLoaded || !scene.isDirty || (prefabStage != null && scene == prefabStage.scene))
                        continue;
                    if (string.IsNullOrEmpty(scene.path))
                    {
                        if (requireSavedPath)
                            throw new InvalidOperationException("Cannot automatically save a scene without an asset path: " + scene.name);
                        continue;
                    }
                    if (!EditorSceneManager.SaveScene(scene))
                        throw new InvalidOperationException("Unity could not save the scene: " + scene.path);
                }
            }
            finally
            {
                saving = false;
            }
        }

        internal static void SaveAfterBridgeMutation()
        {
            CancelScheduledSave();
            SaveCurrentPrefabStageNow();
            SaveOpenSceneChangesNow();
        }

        private static void CancelScheduledSave()
        {
            if (!saveScheduled)
                return;
            saveScheduled = false;
            EditorApplication.update -= SaveDelayed;
        }

        private static void SaveCurrentPrefabStageNow()
        {
            if (saving || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            saving = true;
            try
            {
                PrefabService.SaveCurrentStage();
            }
            finally
            {
                saving = false;
            }
        }

        internal static void Imported(string[] paths)
        {
            var now = EditorApplication.timeSinceStartup;
            foreach (var path in paths ?? Array.Empty<string>())
            {
                if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    continue;
                double savedAt;
                if (UnitySaves.TryGetValue(path, out savedAt) && now - savedAt <= 10.0)
                {
                    UnitySaves.Remove(path);
                    continue;
                }
                UnitySaves.Remove(path);
                var scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded && FileChangedSinceLoad(path))
                    PendingReloads.Add(path);
            }
            ReloadPending();
        }

        private static void OnSceneDirtied(Scene scene)
        {
            if (saving || saveScheduled || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            saveScheduled = true;
            EditorApplication.update += SaveDelayed;
        }

        private static void SaveDelayed()
        {
            EditorApplication.update -= SaveDelayed;
            saveScheduled = false;
            SaveCurrentPrefabStageNow();
            SaveOpenSceneChangesNow(false);
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            if (!string.IsNullOrEmpty(path))
                UnitySaves[path.Replace('\\', '/')] = EditorApplication.timeSinceStartup;
        }

        private static void OnSceneSaved(Scene scene)
        {
            RememberCurrentFile(scene);
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            RememberCurrentFile(scene);
        }

        private static void CheckOpenScenesOnDisk()
        {
            if (EditorApplication.timeSinceStartup < nextDiskCheck || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            nextDiskCheck = EditorApplication.timeSinceStartup + DiskCheckInterval;
            var changedOnDisk = false;
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
                    continue;
                var current = ReadFileStamp(scene.path);
                FileStamp known;
                if (!FileStamps.TryGetValue(scene.path, out known))
                {
                    FileStamps[scene.path] = current;
                    continue;
                }
                if (known.Equals(current))
                    continue;
                FileStamps[scene.path] = current;
                double savedAt;
                if (UnitySaves.TryGetValue(scene.path, out savedAt) && EditorApplication.timeSinceStartup - savedAt <= 10.0)
                {
                    UnitySaves.Remove(scene.path);
                    continue;
                }
                UnitySaves.Remove(scene.path);
                changedOnDisk = true;
                if (FileChangedSinceLoad(scene.path))
                    PendingReloads.Add(scene.path);
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            var reloaded = ReloadPending();
            if (changedOnDisk && !reloaded)
                ClearChangedOnDisk.Invoke(null, null);
        }

        private static bool ReloadPending()
        {
            if (PendingReloads.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode)
                return false;
            var paths = new List<string>(PendingReloads);
            PendingReloads.Clear();
            var reloadedAny = false;
            foreach (var path in paths)
            {
                var scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded)
                {
                    var reloaded = (bool)ReloadScene.Invoke(null, new object[] { scene });
                    if (!reloaded)
                        throw new InvalidOperationException("Unity could not reload the externally modified scene: " + path);
                    reloadedAny = true;
                    RememberCurrentFile(SceneManager.GetSceneByPath(path));
                }
            }
            if (reloadedAny)
                ClearChangedOnDisk.Invoke(null, null);
            return reloadedAny;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                ReloadPending();
        }

        private static bool FileChangedSinceLoad(string assetPath)
        {
            var current = Fingerprint(assetPath);
            var key = FingerprintKeyPrefix + assetPath;
            var known = SessionState.GetString(key, string.Empty);
            if (string.IsNullOrEmpty(known))
            {
                SessionState.SetString(key, current);
                return false;
            }
            return !string.Equals(known, current, StringComparison.Ordinal);
        }

        private static void RememberCurrentFileIfUnknown(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
                return;
            var key = FingerprintKeyPrefix + scene.path;
            if (string.IsNullOrEmpty(SessionState.GetString(key, string.Empty)))
                SessionState.SetString(key, Fingerprint(scene.path));
            FileStamps[scene.path] = ReadFileStamp(scene.path);
        }

        private static void RememberCurrentFile(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
                return;
            SessionState.SetString(FingerprintKeyPrefix + scene.path, Fingerprint(scene.path));
            FileStamps[scene.path] = ReadFileStamp(scene.path);
        }

        private static FileStamp ReadFileStamp(string assetPath)
        {
            var file = new FileInfo(Path.Combine(BridgePaths.ProjectRoot, assetPath));
            return file.Exists ? new FileStamp(file.LastWriteTimeUtc.Ticks, file.Length) : default(FileStamp);
        }

        private static string Fingerprint(string assetPath)
        {
            var fullPath = Path.Combine(BridgePaths.ProjectRoot, assetPath);
            if (!File.Exists(fullPath))
                return string.Empty;
            using (var stream = File.OpenRead(fullPath))
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(stream));
        }

        private struct FileStamp : IEquatable<FileStamp>
        {
            private readonly long ticks;
            private readonly long length;

            public FileStamp(long ticks, long length)
            {
                this.ticks = ticks;
                this.length = length;
            }

            public bool Equals(FileStamp other)
            {
                return ticks == other.ticks && length == other.length;
            }
        }
    }

    internal sealed class SceneExternalChangePostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            ScenePersistenceService.Imported(importedAssets);
        }
    }
}
