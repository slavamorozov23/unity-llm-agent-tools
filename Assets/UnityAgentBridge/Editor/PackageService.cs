using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditorInternal;
using UnityEngine;
using PackageManagerInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnityAgentBridge.Editor
{
    internal static class PackageService
    {
        private const long OperationTimeoutTicks = TimeSpan.TicksPerMinute * 3;
        private static readonly string DomainToken = Guid.NewGuid().ToString("N");
        private static SearchRequest searchRequest;
        private static ListRequest listRequest;
        private static ListRequest resolveListRequest;
        private static bool resolveStarted;
        private static double resolveStartedAt;
        private static AddRequest addRequest;
        private static AddAndRemoveRequest addAndRemoveRequest;
        private static Request removeRequest;
        private static string addIdentifier;
        private static string removeName;

        [Serializable]
        private sealed class PersistedOperation
        {
            public string kind;
            public string identifier;
            public string packageName;
            public string requestedVersion;
            public bool wasDirect;
            public string previousVersion;
            public string[] identifiers;
            public string[] packageNames;
            public string[] previousPackages;
            public bool inputHandlerChanged;
            public int previousInputHandler;
            public string domainToken;
            public long startedUtcTicks;
        }

        [Serializable]
        private sealed class PackageManifest
        {
            public string unity;
            public string unityRelease;
        }

        public static PackageData[] List()
        {
            return PackageManagerInfo.GetAllRegisteredPackages()
                .OrderBy(package => package.name, StringComparer.Ordinal)
                .Select(Convert)
                .ToArray();
        }

        public static void List(BridgeResponse response)
        {
            if (listRequest == null)
            {
                listRequest = Client.List(false, true);
                response.pending = true;
                return;
            }
            if (!listRequest.IsCompleted)
            {
                response.pending = true;
                return;
            }
            var completed = listRequest;
            listRequest = null;
            EnsureSuccess(completed);
            response.packages = completed.Result.OrderBy(package => package.name, StringComparer.Ordinal).Select(Convert).ToArray();
        }

        public static void Resolve(BridgeResponse response)
        {
            if (!resolveStarted)
            {
                Client.Resolve();
                resolveStarted = true;
                resolveStartedAt = EditorApplication.timeSinceStartup;
                response.pending = true;
                return;
            }
            if (resolveListRequest == null)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                    EditorApplication.timeSinceStartup - resolveStartedAt < 0.5d)
                {
                    response.pending = true;
                    return;
                }
                try
                {
                    resolveListRequest = Client.List(false, true);
                }
                catch (InvalidOperationException)
                {
                    response.pending = true;
                    return;
                }
                response.pending = true;
                return;
            }
            if (!resolveListRequest.IsCompleted)
            {
                response.pending = true;
                return;
            }
            var completed = resolveListRequest;
            resolveListRequest = null;
            resolveStarted = false;
            EnsureSuccess(completed);
            response.packages = completed.Result.OrderBy(package => package.name, StringComparer.Ordinal).Select(Convert).ToArray();
            response.message = "Packages resolved.";
        }

        internal static bool IsInstalled(string name)
        {
            return FindInstalled(name) != null;
        }

        internal static int ActiveInputHandler
        {
            get { return ReadActiveInputHandler(); }
        }

        public static void Search(BridgeResponse response)
        {
            if (searchRequest == null)
            {
                UnityEditor.PackageManager.UI.Window.Open(string.Empty);
                searchRequest = Client.SearchAll(false);
                response.pending = true;
                return;
            }
            if (!searchRequest.IsCompleted)
            {
                response.pending = true;
                return;
            }
            var completed = searchRequest;
            searchRequest = null;
            EnsureSuccess(completed);
            response.packages = completed.Result.Select(Convert).ToArray();
        }

        public static void AddOrUpdate(BridgeResponse response, string name, string version)
        {
            AddOrUpdate(response, new[] { name }, version);
            if (response.packages != null && response.packages.Length == 1)
                response.package = response.packages[0];
        }

        public static void AddOrUpdate(BridgeResponse response, string[] names, string version)
        {
            // "name@1.2.3", as Package Manager's Add package by name takes it; --version is the same for one package.
            var entries = RequiredNames(names);
            version = OptionalVersion(version);
            if (entries.Length > 1 && version != null)
                throw new ArgumentException("A shared version is only valid for one package; use name@version.", "version");
            if (version != null && entries[0].Contains("@"))
                throw new ArgumentException("Give the version either as name@version or with --version.", "version");
            names = entries.Select(entry => RequiredName(entry.Split('@')[0])).ToArray();
            var identifiers = entries.Select((entry, index) => Identifier(names[index],
                entry.Contains("@") ? OptionalVersion(entry.Substring(entry.IndexOf('@') + 1)) : version)).ToArray();
            if (names.Length == 1 && version == null && entries[0].Contains("@"))
                version = identifiers[0].Substring(names[0].Length + 1);
            var identifier = string.Join("|", identifiers);
            var state = CurrentOperation("add", identifier);
            if (state != null)
            {
                if (addRequest == null && addAndRemoveRequest == null && AddReachedAfterReload(state))
                {
                    var installed = names.Select(FindInstalled).Where(package => package != null).ToArray();
                    UnityEditor.PackageManager.UI.Window.Open(names[0]);
                    CompleteAdd(response, state, installed);
                    ClearOperation();
                    return;
                }
                EnsureNotExpired(state);
            }

            if (addRequest == null && addAndRemoveRequest == null)
            {
                if (state != null)
                {
                    response.pending = true;
                    return;
                }
                if (removeRequest != null)
                    throw new InvalidOperationException("Another package operation is still running.");

                var installed = names.Select(FindInstalled).ToArray();
                state = new PersistedOperation
                {
                    kind = "add",
                    identifier = identifier,
                    packageName = names[0],
                    packageNames = names,
                    identifiers = identifiers,
                    requestedVersion = version,
                    wasDirect = installed[0] != null && installed[0].isDirectDependency,
                    previousVersion = installed[0] == null ? null : installed[0].version,
                    previousPackages = SnapshotPackages(),
                    domainToken = DomainToken,
                    startedUtcTicks = DateTime.UtcNow.Ticks
                };
                if (names.Any(name => string.Equals(name, "com.unity.inputsystem", StringComparison.Ordinal)))
                {
                    state.previousInputHandler = ReadActiveInputHandler();
                    state.inputHandlerChanged = state.previousInputHandler != 2;
                }
                SaveOperation(state);
                try
                {
                    if (state.inputHandlerChanged)
                        WriteActiveInputHandler(2);
                    UnityEditor.PackageManager.UI.Window.Open(names[0]);
                    addIdentifier = identifier;
                    if (identifiers.Length == 1)
                        addRequest = Client.Add(identifier);
                    else
                        addAndRemoveRequest = Client.AddAndRemove(identifiers, Array.Empty<string>());
                }
                catch
                {
                    RestoreInputHandler(state);
                    ClearOperation();
                    throw;
                }
                response.pending = true;
                return;
            }

            if (!string.Equals(addIdentifier, identifier, StringComparison.Ordinal))
                throw new InvalidOperationException("Another package operation is still running: " + addIdentifier);
            if (addRequest == null && addAndRemoveRequest == null)
            {
                response.pending = true;
                return;
            }
            if ((addRequest != null && !addRequest.IsCompleted) ||
                (addAndRemoveRequest != null && !addAndRemoveRequest.IsCompleted))
            {
                response.pending = true;
                return;
            }

            var completedAdd = addRequest;
            var completedBatch = addAndRemoveRequest;
            addRequest = null;
            addAndRemoveRequest = null;
            addIdentifier = null;
            try
            {
                if (completedAdd != null)
                    EnsureSuccess(completedAdd);
                else
                    EnsureSuccess(completedBatch);
                UnityEditor.PackageManager.UI.Window.Open(names[0]);
                var installed = completedAdd != null
                    ? new[] { completedAdd.Result }
                    : completedBatch.Result.Where(package => names.Contains(package.name, StringComparer.Ordinal)).ToArray();
                CompleteAdd(response, state, installed);
                ClearOperation();
            }
            catch
            {
                RestoreInputHandler(state ?? LoadOperation());
                ClearOperation();
                throw;
            }
        }

        public static void Remove(BridgeResponse response, string[] names)
        {
            names = RequiredNames(names).Select(RequiredName).ToArray();
            var name = string.Join("|", names);
            var removed = "Package removed: " + string.Join(", ", names);
            var state = CurrentOperation("remove", name);
            if (state != null)
            {
                if (removeRequest == null && RemoveReachedAfterReload(state))
                {
                    ClearOperation();
                    response.message = removed;
                    return;
                }
                EnsureNotExpired(state);
            }

            if (removeRequest == null)
            {
                if (state != null)
                {
                    response.pending = true;
                    return;
                }
                if (addRequest != null || addAndRemoveRequest != null)
                    throw new InvalidOperationException("Another package operation is still running.");
                var installed = FindInstalled(names[0]);
                state = new PersistedOperation
                {
                    kind = "remove",
                    identifier = name,
                    packageName = names[0],
                    packageNames = names,
                    wasDirect = installed != null && installed.isDirectDependency,
                    previousVersion = installed == null ? null : installed.version,
                    domainToken = DomainToken,
                    startedUtcTicks = DateTime.UtcNow.Ticks
                };
                SaveOperation(state);
                try
                {
                    UnityEditor.PackageManager.UI.Window.Open(names[0]);
                    removeName = name;
                    removeRequest = names.Length == 1 ? (Request)Client.Remove(names[0]) : Client.AddAndRemove(Array.Empty<string>(), names);
                }
                catch
                {
                    ClearOperation();
                    throw;
                }
                response.pending = true;
                return;
            }

            if (!string.Equals(removeName, name, StringComparison.Ordinal))
                throw new InvalidOperationException("Another package operation is still running: " + removeName);
            if (!removeRequest.IsCompleted)
            {
                response.pending = true;
                return;
            }

            var completed = removeRequest;
            removeRequest = null;
            removeName = null;
            try
            {
                EnsureSuccess(completed);
                response.message = removed;
                ClearOperation();
            }
            catch
            {
                ClearOperation();
                throw;
            }
        }

        private static bool AddReachedAfterReload(PersistedOperation state)
        {
            if (string.Equals(state.domainToken, DomainToken, StringComparison.Ordinal))
                return false;
            var names = state.packageNames != null && state.packageNames.Length > 0
                ? state.packageNames
                : new[] { state.packageName };
            for (var index = 0; index < names.Length; index++)
            {
                var installed = FindInstalled(names[index]);
                if (installed == null || !installed.isDirectDependency)
                    return false;
                var identifier = state.identifiers != null && index < state.identifiers.Length ? state.identifiers[index] : null;
                var requested = identifier != null && identifier.Contains("@") ? identifier.Substring(identifier.IndexOf('@') + 1) : state.requestedVersion;
                if (!string.IsNullOrEmpty(requested) && !string.Equals(installed.version, requested, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static void CompleteAdd(BridgeResponse response, PersistedOperation state, PackageManagerInfo[] installed)
        {
            response.packages = installed.Select(Convert).ToArray();
            response.packageChanges = PackageChanges(state == null ? null : state.previousPackages, installed.Select(package => package.name));
        }

        private static string[] SnapshotPackages()
        {
            return PackageManagerInfo.GetAllRegisteredPackages()
                .Select(package => package.name + "\t" + package.version)
                .ToArray();
        }

        private static PackageChangeData[] PackageChanges(string[] previous, IEnumerable<string> requestedNames)
        {
            var requested = new HashSet<string>(requestedNames, StringComparer.Ordinal);
            var before = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in previous ?? Array.Empty<string>())
            {
                var separator = entry.IndexOf('\t');
                if (separator > 0)
                    before[entry.Substring(0, separator)] = entry.Substring(separator + 1);
            }
            return PackageManagerInfo.GetAllRegisteredPackages()
                .Where(package => !requested.Contains(package.name) &&
                    (!before.TryGetValue(package.name, out var oldVersion) || !string.Equals(oldVersion, package.version, StringComparison.Ordinal)))
                .OrderBy(package => package.name, StringComparer.Ordinal)
                .Select(package => new PackageChangeData
                {
                    name = package.name,
                    previousVersion = before.TryGetValue(package.name, out var oldVersion) ? oldVersion : null,
                    version = package.version
                })
                .ToArray();
        }

        private static bool RemoveReachedAfterReload(PersistedOperation state)
        {
            if (string.Equals(state.domainToken, DomainToken, StringComparison.Ordinal))
                return false;
            var names = state.packageNames != null && state.packageNames.Length > 0 ? state.packageNames : new[] { state.packageName };
            return names.Select(FindInstalled).All(installed => installed == null || !installed.isDirectDependency);
        }

        // The operation this call continues; one left by an earlier call that has finished in the meantime
        // (a reload dropped its request) or run past the timeout no longer blocks other package commands.
        private static PersistedOperation CurrentOperation(string kind, string identifier)
        {
            var state = LoadOperation();
            if (state == null || string.Equals(state.kind, kind, StringComparison.Ordinal) && string.Equals(state.identifier, identifier, StringComparison.Ordinal))
                return state;
            var idle = addRequest == null && addAndRemoveRequest == null && removeRequest == null;
            var finished = idle && (state.kind == "add" ? AddReachedAfterReload(state) : RemoveReachedAfterReload(state));
            var abandoned = DateTime.UtcNow.Ticks - state.startedUtcTicks > OperationTimeoutTicks;
            if (!finished && !abandoned)
                throw new InvalidOperationException("Another package operation is still running: " + state.identifier);
            ClearOperation();
            return null;
        }

        private static PackageManagerInfo FindInstalled(string name)
        {
            return PackageManagerInfo.GetAllRegisteredPackages()
                .FirstOrDefault(package => string.Equals(package.name, name, StringComparison.Ordinal));
        }

        private static void EnsureNotExpired(PersistedOperation state)
        {
            if (DateTime.UtcNow.Ticks - state.startedUtcTicks <= OperationTimeoutTicks)
                return;
            ClearOperation();
            throw new TimeoutException("Unity Package Manager operation did not finish within 180 seconds.");
        }

        private static string Identifier(string name, string version)
        {
            return string.IsNullOrEmpty(version) ? name : name + "@" + version;
        }

        private static string OptionalVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return null;
            version = version.Trim();
            if (version.IndexOfAny(new[] { '@', ' ', '\t', '\r', '\n' }) >= 0)
                throw new ArgumentException("Package version is invalid.", "version");
            return version;
        }

        private static string RequiredName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Package name is required.", "name");
            name = name.Trim();
            if (name.IndexOfAny(new[] { '@', ' ', '\t', '\r', '\n' }) >= 0)
                throw new ArgumentException("Package name is invalid.", "name");
            return name;
        }

        private static string[] RequiredNames(string[] names)
        {
            if (names == null || names.Length == 0)
                throw new ArgumentException("At least one package name is required.", "names");
            var result = names.SelectMany(name => (name ?? string.Empty).Split(','))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (result.Length == 0)
                throw new ArgumentException("At least one package name is required.", "names");
            return result;
        }

        private static PackageData Convert(PackageManagerInfo package)
        {
            if (package == null)
                throw new InvalidOperationException("Package Manager completed without registering the package.");
            var dependencies = package.resolvedDependencies ?? Array.Empty<DependencyInfo>();
            return new PackageData
            {
                name = package.name,
                displayName = package.displayName,
                version = package.version,
                description = package.description,
                source = package.source.ToString(),
                direct = package.isDirectDependency,
                dependencies = dependencies.Select(dependency => new PackageDependencyData
                {
                    name = dependency.name,
                    version = dependency.version
                }).ToArray(),
                minimumUnity = MinimumUnity(package),
                compatible = CompatibleWithCurrentEditor(package)
            };
        }

        private static string MinimumUnity(PackageManagerInfo package)
        {
            var manifest = ReadManifest(package);
            if (manifest == null || string.IsNullOrEmpty(manifest.unity))
                return null;
            return manifest.unity + (string.IsNullOrEmpty(manifest.unityRelease) ? string.Empty : "." + manifest.unityRelease);
        }

        private static bool CompatibleWithCurrentEditor(PackageManagerInfo package)
        {
            if (package.versions != null && package.versions.compatible != null && package.versions.compatible.Length > 0)
                return package.versions.compatible.Contains(package.version, StringComparer.Ordinal);
            var manifest = ReadManifest(package);
            if (manifest == null || string.IsNullOrEmpty(manifest.unity))
                return true;
            var required = manifest.unity.Split('.');
            var current = Application.unityVersion.Split('.');
            if (required.Length < 2 || current.Length < 2 ||
                !int.TryParse(required[0], out var requiredMajor) || !int.TryParse(required[1], out var requiredMinor) ||
                !int.TryParse(current[0], out var currentMajor) || !int.TryParse(current[1], out var currentMinor))
                return true;
            return currentMajor > requiredMajor || currentMajor == requiredMajor && currentMinor >= requiredMinor;
        }

        private static PackageManifest ReadManifest(PackageManagerInfo package)
        {
            if (string.IsNullOrEmpty(package.resolvedPath))
                return null;
            var path = Path.Combine(package.resolvedPath, "package.json");
            if (!File.Exists(path))
                return null;
            return JsonUtility.FromJson<PackageManifest>(File.ReadAllText(path));
        }

        private static void EnsureSuccess(Request request)
        {
            if (request.Status == StatusCode.Success)
                return;
            var message = request.Error == null ? "Package Manager operation failed." : request.Error.message;
            throw new InvalidOperationException(message);
        }

        private static string OperationPath
        {
            get { return Path.Combine(BridgePaths.RuntimeRoot, "package-operation.json"); }
        }

        private static PersistedOperation LoadOperation()
        {
            if (!File.Exists(OperationPath))
                return null;
            try
            {
                var state = JsonUtility.FromJson<PersistedOperation>(File.ReadAllText(OperationPath));
                if (state == null || string.IsNullOrEmpty(state.kind) || string.IsNullOrEmpty(state.identifier))
                    throw new InvalidDataException("Package operation state is invalid.");
                return state;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Package operation state cannot be read: " + exception.Message, exception);
            }
        }

        private static void SaveOperation(PersistedOperation state)
        {
            Directory.CreateDirectory(BridgePaths.RuntimeRoot);
            var temporary = OperationPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(state, false));
            if (File.Exists(OperationPath))
                File.Delete(OperationPath);
            File.Move(temporary, OperationPath);
        }

        private static void ClearOperation()
        {
            if (File.Exists(OperationPath))
                File.Delete(OperationPath);
            var temporary = OperationPath + ".tmp";
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        private static int ReadActiveInputHandler()
        {
            var path = Path.GetFullPath("ProjectSettings/ProjectSettings.asset");
            var assets = InternalEditorUtility.LoadSerializedFileAndForget(path);
            if (assets == null || assets.Length == 0)
                throw new InvalidOperationException("ProjectSettings.asset could not be loaded.");
            try
            {
                var serialized = new SerializedObject(assets[0]);
                var property = serialized.FindProperty("activeInputHandler");
                if (property == null)
                    throw new MissingMemberException("ProjectSettings", "activeInputHandler");
                return property.intValue;
            }
            finally
            {
                Destroy(assets);
            }
        }

        private static void WriteActiveInputHandler(int value)
        {
            var path = Path.GetFullPath("ProjectSettings/ProjectSettings.asset");
            var assets = InternalEditorUtility.LoadSerializedFileAndForget(path);
            if (assets == null || assets.Length == 0)
                throw new InvalidOperationException("ProjectSettings.asset could not be loaded.");
            try
            {
                var serialized = new SerializedObject(assets[0]);
                var property = serialized.FindProperty("activeInputHandler");
                if (property == null)
                    throw new MissingMemberException("ProjectSettings", "activeInputHandler");
                property.intValue = value;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                InternalEditorUtility.SaveToSerializedFileAndForget(assets, path, true);
            }
            finally
            {
                Destroy(assets);
            }
        }

        private static void RestoreInputHandler(PersistedOperation state)
        {
            if (state != null && state.inputHandlerChanged)
                WriteActiveInputHandler(state.previousInputHandler);
        }

        private static void Destroy(UnityEngine.Object[] assets)
        {
            foreach (var asset in assets)
                if (asset != null)
                    UnityEngine.Object.DestroyImmediate(asset);
        }
    }

    internal static class InputSystemSetupService
    {
        internal const string PackageName = "com.unity.inputsystem";

        internal static bool IsReady
        {
            get { return PackageService.IsInstalled(PackageName) && PackageService.ActiveInputHandler == 2; }
        }

        internal static string Status
        {
            get
            {
                if (!PackageService.IsInstalled(PackageName))
                    return "Setup required";
                return PackageService.ActiveInputHandler == 2 ? "Ready" : "Enable Both input backends";
            }
        }

        internal static bool PollSetup()
        {
            var response = new BridgeResponse();
            PackageService.AddOrUpdate(response, PackageName, null);
            return !response.pending && IsReady;
        }

        internal static void RequireReady()
        {
            if (!IsReady)
                throw new InvalidOperationException(
                    "Game input is not configured. Open Tools > Unity Agent Bridge and install the required Input System setup.");
        }
    }
}
