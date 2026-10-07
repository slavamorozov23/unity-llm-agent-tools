using System;
using System.Globalization;
using System.IO;
using UnityEditor;

namespace UnityAgentBridge.Editor
{
    // A source file written on disk while Unity was not refreshing (edited outside the editor) still has its old import,
    // so errors reported for it are old too.
    internal sealed class SourceFreshness : AssetPostprocessor
    {
        private const string Prefix = "UnityAgentBridge.Imported:";

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var now = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            foreach (var path in imported)
                SessionState.SetString(Prefix + path, now);
        }

        internal static bool Changed(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            long ticks;
            var imported = long.TryParse(SessionState.GetString(Prefix + path, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)
                ? new DateTime(ticks, DateTimeKind.Utc)
                : DateTime.UtcNow.AddSeconds(-EditorApplication.timeSinceStartup);
            return File.GetLastWriteTimeUtc(path) > imported.AddSeconds(1);
        }
    }
}
