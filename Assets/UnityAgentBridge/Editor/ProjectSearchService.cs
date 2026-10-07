using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // asset-find and object-find --ref: the Unity Search window's own engine and query, shown in that window.
    [InitializeOnLoad]
    internal static class ProjectSearchService
    {
        private const string SearchWindow = "UnityEditor.Search.SearchWindow";

        // Each script reload reopens the index of an open Search window without closing the old one, and after
        // dozens of reloads Unity runs out of index handles (MDB_TLS_FULL) and a search never ends. Search windows close
        // before a reload; the next search opens the tab again next to the Project window.
        static ProjectSearchService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                var windowType = typeof(SearchService).Assembly.GetType(SearchWindow, false);
                if (windowType != null)
                    foreach (EditorWindow window in Resources.FindObjectsOfTypeAll(windowType))
                        window.Close();
            };
        }

        [Serializable]
        private sealed class AssetItem
        {
            public string path;
            public string name;
            public string type;
        }

        [Serializable]
        private sealed class AssetResult
        {
            public AssetItem[] assets;
        }

        [Serializable]
        private sealed class ObjectResult
        {
            public string[] paths;
        }

        public static string Assets(string query)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var assets = new List<AssetItem>();
            var subAssets = new Dictionary<string, Dictionary<string, Type>>(StringComparer.Ordinal);
            // With t: the search has already matched the type; loading every model to read its meshes took minutes.
            var filtered = Regex.Match(query, @"\bt:(\w+)");
            // The index also lags behind edits: a reference removed a moment ago is still found, so each hit is checked.
            var referenced = Regex.Match(query, "\\bref=\"([^\"]+)\"");
            foreach (var found in Fetch(query, item => new KeyValuePair<string, string>(item.id, item.GetLabel(item.context))))
            {
                GlobalObjectId id;
                var path = GlobalObjectId.TryParse(found.Key, out id) ? AssetDatabase.GUIDToAssetPath(id.assetGUID) : found.Key;
                // The search index can lag behind deleted assets.
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || !seen.Add(path + "|" + found.Value))
                    continue;
                if (referenced.Success && !AssetDatabase.GetDependencies(path, false).Contains(referenced.Groups[1].Value.Split('#')[0]))
                    continue;
                var main = AssetDatabase.GetMainAssetTypeAtPath(path);
                var type = !IsSubAsset(path, found.Value) ? (main == null ? string.Empty : main.Name) :
                    filtered.Success ? filtered.Groups[1].Value : SubAssetType(path, found.Value, subAssets) ?? (main == null ? string.Empty : main.Name);
                assets.Add(new AssetItem { path = path, name = found.Value, type = type });
            }
            Show(query);
            return JsonUtility.ToJson(new AssetResult { assets = assets.ToArray() });
        }

        // A search item named unlike its file is a sub-asset, such as a Sprite inside a texture.
        private static bool IsSubAsset(string path, string name)
        {
            return name != Path.GetFileName(path) && name != Path.GetFileNameWithoutExtension(path);
        }

        private static string SubAssetType(string path, string name, Dictionary<string, Dictionary<string, Type>> cache)
        {
            Dictionary<string, Type> types;
            if (!cache.TryGetValue(path, out types))
            {
                types = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path).Where(asset => asset != null))
                    types[asset.name] = asset.GetType();
                cache[path] = types;
            }
            Type type;
            return types.TryGetValue(name, out type) ? type.Name : null;
        }

        // Find References In Scene: the loaded scenes' objects that use the asset.
        public static string SceneReferences(string assetPath)
        {
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null)
                throw new InvalidOperationException("Asset was not found: " + assetPath);
            var query = "h: ref=\"" + assetPath + "\"";
            var paths = Fetch(query, item => item.ToObject())
                .Select(target => target as GameObject ?? (target as Component)?.gameObject)
                .Where(gameObject => gameObject != null && gameObject.scene.IsValid())
                .Select(ScenePath.For)
                .Distinct()
                .ToArray();
            Show(query);
            return JsonUtility.ToJson(new ObjectResult { paths = paths });
        }

        private const double SearchTimeoutSeconds = 90d;
        private static string pendingQuery;
        private static SearchContext pendingContext;
        private static IList pendingResult;
        private static double pendingDeadline;

        // The search runs as it does in the Search window, while the editor keeps updating: a synchronous request
        // froze Unity for good when its index could not load. The command is retried until the results arrive.
        private static List<T> Fetch<T>(string query, Func<SearchItem, T> select)
        {
            if (pendingQuery != query || pendingContext == null)
            {
                EndPending();
                pendingQuery = query;
                pendingDeadline = EditorApplication.timeSinceStartup + SearchTimeoutSeconds;
                var context = pendingContext = SearchService.CreateContext(query);
                SearchService.Request(context, (done, items) =>
                {
                    if (done == pendingContext)
                        pendingResult = items.Select(select).ToList();
                });
            }
            if (pendingResult == null)
            {
                if (EditorApplication.timeSinceStartup < pendingDeadline)
                    throw new BridgeNotReadyException("Unity Search is still searching.");
                EndPending();
                throw new TimeoutException("Unity Search did not finish within " + SearchTimeoutSeconds +
                    " s; its index is likely broken (Console: LMDB errors). Restarting Unity rebuilds it.");
            }
            var result = (List<T>)pendingResult;
            EndPending();
            return result;
        }

        private static void EndPending()
        {
            if (pendingContext != null)
                pendingContext.Dispose();
            pendingContext = null;
            pendingResult = null;
            pendingQuery = null;
        }

        private static void Show(string query)
        {
            // The query goes into the docked Search tab; SearchService.ShowWindow floated a second window over the editor.
            var view = EditorPresentationService.ShowWindow(SearchWindow, "UnityEditor.ProjectBrowser") as ISearchView;
            if (view != null)
                view.SetSearchText(query);
        }
    }
}
