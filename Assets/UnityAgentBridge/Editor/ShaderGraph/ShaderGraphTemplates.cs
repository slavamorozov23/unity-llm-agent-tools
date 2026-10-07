using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // Assets > Create > Shader Graph > From Template... as creation templates: "Create/Shader Graph/From Template/<category>/<name>",
    // with the template's description as search keywords. A template is created the way the template window does it.
    internal static class ShaderGraphTemplates
    {
        private const string Prefix = "Create/Shader Graph/From Template/";

        internal static void Register()
        {
            // Shader Graph asks these once with a modal dialog; the defaults it offers are answered up front
            // (both stay editable in Preferences > Shader Graph).
            if (!EditorPrefs.HasKey("UnityEditor.ShaderGraph.OpenNewGraphOnCreation"))
                EditorPrefs.SetBool("UnityEditor.ShaderGraph.OpenNewGraphOnCreation", true);
            if (!EditorPrefs.HasKey("UnityEditor.ShaderGraph.GraphTemplateWorkflow"))
                EditorPrefs.SetInt("UnityEditor.ShaderGraph.GraphTemplateWorkflow", 0);
            AssetService.PackageTemplates.RemoveAll(source => source.list == List);
            AssetService.PackageTemplates.Add(new AssetService.TemplateSource { list = List, create = Create });
        }

        private static IEnumerable<CreationTemplateData> List()
        {
            return Templates().Select(item => new CreationTemplateData { name = item.Key, extension = ".shadergraph", keywords = Summary(item.Value.Value) });
        }

        // "A simple Lit shader that ... | HDRP & URP": the first sentence and the supported pipelines of the template card.
        private static string Summary(string description)
        {
            if (string.IsNullOrEmpty(description))
                return null;
            var text = System.Text.RegularExpressions.Regex.Replace(description, "<[^>]+>", string.Empty);
            var sentence = text.Split(new[] { ". ", ".\n" }, StringSplitOptions.None)[0].Trim().TrimEnd('.');
            var pipelines = System.Text.RegularExpressions.Regex.Match(text, @"Supported Pipeline\(s\):\s*([^\n]+)");
            return sentence + (pipelines.Success ? " | " + pipelines.Groups[1].Value.Trim() : string.Empty);
        }

        // name -> (template file, description)
        private static IEnumerable<KeyValuePair<string, KeyValuePair<string, string>>> Templates()
        {
            foreach (var path in AssetDatabase.GetAllAssetPaths().Where(item => item.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)))
            {
                var importer = AssetImporter.GetAtPath(path);
                if (importer == null || !Sg.Has(importer, "UseAsTemplate") || !(bool)Sg.Get(importer, "UseAsTemplate"))
                    continue;
                var template = Sg.Get(importer, "Template");
                var name = template == null ? null : (string)Sg.Get(template, "name");
                var category = template == null ? null : (string)Sg.Get(template, "category");
                var description = template == null ? null : (string)Sg.Get(template, "description");
                yield return new KeyValuePair<string, KeyValuePair<string, string>>(
                    Prefix + (string.IsNullOrEmpty(category) ? "uncategorized" : category) + "/" + (string.IsNullOrEmpty(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name),
                    new KeyValuePair<string, string>(path, description));
            }
        }

        private static bool Create(string templateName, string assetPath)
        {
            var template = Templates().FirstOrDefault(item => string.Equals(item.Key, templateName, StringComparison.OrdinalIgnoreCase));
            if (template.Key == null)
                return false;
            // The template window's own end action copies the template to the chosen path.
            var action = ScriptableObject.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.NewGraphFromTemplateAction"));
            try
            {
                Sg.Call(action, "CreateAndRenameGraphFromTemplate", template.Value.Key, assetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(action);
            }
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null)
                throw new InvalidOperationException("Shader Graph created no asset from template " + templateName + ".");
            return true;
        }
    }
}
