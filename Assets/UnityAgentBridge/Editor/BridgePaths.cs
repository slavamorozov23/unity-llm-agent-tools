using System.IO;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class BridgePaths
    {
        public const string StandardPrefabFolder = "Assets/UnityAgentBridge/Prefabs";

        public static string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        public static string RuntimeRoot
        {
            get { return Path.Combine(ProjectRoot, "Library", "UnityAgentBridge"); }
        }

        public static string Requests
        {
            get { return Path.Combine(RuntimeRoot, "Requests"); }
        }

        public static string Responses
        {
            get { return Path.Combine(RuntimeRoot, "Responses"); }
        }

        public static void EnsureRuntimeDirectories()
        {
            Directory.CreateDirectory(Requests);
            Directory.CreateDirectory(Responses);
        }
    }
}
