using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityAgentBridge.Editor
{
    internal static class MenuService
    {
        private static readonly string[] BuiltInRoots = { "File", "Edit", "Assets", "GameObject", "Component", "Window", "Help" };

        public static string[] ListProject()
        {
            var projectAssemblies = new HashSet<string>(CompilationPipeline.GetAssemblies()
                .Where(assembly => assembly.sourceFiles.Any(file => file.StartsWith("Assets/", StringComparison.Ordinal)))
                .Select(assembly => assembly.name), StringComparer.Ordinal);
            var items = new HashSet<string>(List(), StringComparer.Ordinal);
            return TypeCache.GetMethodsWithAttribute<MenuItem>()
                .Where(method => projectAssemblies.Contains(method.DeclaringType.Assembly.GetName().Name))
                .SelectMany(method => method.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>())
                .Where(item => !item.validate && items.Contains(item.menuItem))
                .Select(item => item.menuItem)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        public static string[] List()
        {
            var roots = BuiltInRoots.Concat(TypeCache.GetMethodsWithAttribute<MenuItem>()
                    .SelectMany(method => method.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>())
                    .Select(item => item.menuItem.Split('/')[0]))
                .Where(root => root.Length > 0 && root != "CONTEXT" && !root.StartsWith("internal:", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal);
            return roots
                .SelectMany(Submenus)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        internal static string[] Submenus(string root)
        {
            var unsupported = typeof(EditorApplication).Assembly.GetType("UnityEditor.Unsupported", true);
            var submenus = unsupported.GetMethod("GetSubmenus", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);
            if (submenus == null)
                throw new MissingMethodException("Unity does not expose its menu items.");
            return (submenus.Invoke(null, new object[] { root }) as string[]) ?? Array.Empty<string>();
        }

        public static string Execute(string path, string requestId)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Menu path is required.", "path");
            var trimmed = path.Trim().Trim('/');
            var items = List();
            var match = items.FirstOrDefault(item => string.Equals(item, trimmed, StringComparison.Ordinal)) ??
                items.FirstOrDefault(item => string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new InvalidOperationException("Menu item was not found: " + trimmed);

            var directory = Path.Combine(BridgePaths.RuntimeRoot, "Menu");
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, requestId + ".state");
            File.WriteAllText(marker, "scheduled:" + match);
            EditorPresentationService.NextUpdate(() => Run(match, marker));
            return marker;
        }

        private static void Run(string path, string marker)
        {
            try
            {
                File.WriteAllText(marker, "running:" + path);
                if (!EditorApplication.ExecuteMenuItem(path))
                    throw new InvalidOperationException("Unity did not execute menu item (disabled in the current state): " + path);
                File.WriteAllText(marker, "complete");
            }
            catch (Exception exception)
            {
                File.WriteAllText(marker, "error:" + exception.Message);
            }
        }
    }
}
