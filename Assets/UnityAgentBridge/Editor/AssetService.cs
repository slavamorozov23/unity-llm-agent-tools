using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAgentBridge.Editor
{
    internal static class AssetService
    {
        private const string ScriptableObjectTemplatePrefix = "ScriptableObject: ";

        [Serializable]
        private sealed class ShaderInfoData
        {
            public string name;
            public string path;
            public string shader;
            public string shaderPath;
            public ShaderPropertyInfoData[] properties;
            public string[] defaults;
            public ShaderErrorData[] errors;
        }

        private sealed class ExpectedAssetValue
        {
            public string source;
            public string path;
            public string value;
        }

        [Serializable]
        private sealed class ShaderPropertyInfoData
        {
            public string name;
            public string type;
            public string value;
        }

        [Serializable]
        private sealed class ShaderErrorData
        {
            public string severity;
            public string message;
            public string file;
            public int line;
        }

        [Serializable]
        private sealed class JsonStringData { public string value; }

        [Serializable]
        private sealed class ShaderColorData
        {
            public float r;
            public float g;
            public float b;
            public float a = 1f;
        }

        [Serializable]
        private sealed class ShaderVectorData
        {
            public float x;
            public float y;
            public float z;
            public float w;
        }
        private static readonly CreationTemplateData[] Templates =
        {
            new CreationTemplateData { name = "C# Script", extension = ".cs" },
            new CreationTemplateData { name = "C# Editor Script", extension = ".cs" },
            new CreationTemplateData { name = "ScriptableObject Script", extension = ".cs" },
            new CreationTemplateData { name = "Text File", extension = ".txt" },
            new CreationTemplateData { name = "JSON File", extension = ".json" },
            new CreationTemplateData { name = "Shader", extension = ".shader" },
            new CreationTemplateData { name = "Compute Shader", extension = ".compute" },
            new CreationTemplateData { name = "Animator Controller", extension = ".controller" },
            new CreationTemplateData { name = "Animation Clip", extension = ".anim" },
            new CreationTemplateData { name = "Material", extension = ".mat" },
            new CreationTemplateData { name = "Scene", extension = ".unity" }
        };

        // Assets > Create > Timeline, offered only when the Timeline package is installed.
        private static readonly Type TimelineAssetType = Type.GetType("UnityEngine.Timeline.TimelineAsset, Unity.Timeline", false);

        private static IEnumerable<CreationTemplateData> BuiltInTemplates()
        {
            return TimelineAssetType == null
                ? Templates
                : Templates.Append(new CreationTemplateData { name = "Timeline", extension = ".playable" });
        }

        // With a path, the things that asset's window creates (Shader Graph: the Create Node entries).
        public static CreationTemplateData[] ListCreationTemplates(string path = null)
        {
            if (!string.IsNullOrEmpty(path))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                var view = AssetViews.Find(path, asset);
                var templates = view == null ? null : view.Templates(path, asset);
                if (templates == null)
                    throw new InvalidOperationException("This asset has no creation templates: " + path);
                return templates;
            }
            return BuiltInTemplates().Concat(MenuTemplates()).Concat(ScriptableObjectTemplates()).Concat(PackageTemplates.SelectMany(source => source.list())).ToArray();
        }

        // Package assemblies add what their own "..." windows create (Shader Graph: From Template/<category>/<name>).
        internal sealed class TemplateSource
        {
            internal Func<IEnumerable<CreationTemplateData>> list;
            // Creates the asset at the path and returns true, or returns false for another source's template.
            internal Func<string, string, bool> create;
        }

        internal static readonly List<TemplateSource> PackageTemplates = new List<TemplateSource>();

        // Assets > Create items as "Create/<menu path>", except items that write scripts or open a window.
        private const string MenuTemplatePrefix = "Create/";
        private static readonly Regex MenuTemplateExcluded = new Regex(@"Script|C#|Assembly Definition|Editor Window|\.\.\.$|/Folder$|From Scene$", RegexOptions.IgnoreCase);

        private static IEnumerable<CreationTemplateData> MenuTemplates()
        {
            var builtIn = new HashSet<string>(BuiltInTemplates().Select(template => template.name), StringComparer.OrdinalIgnoreCase);
            return MenuService.Submenus("Assets")
                .Where(path => path.StartsWith("Assets/" + MenuTemplatePrefix, StringComparison.Ordinal) && !MenuTemplateExcluded.IsMatch(path) &&
                    !builtIn.Contains(path.Substring(path.LastIndexOf('/') + 1)))
                .Select(path => new CreationTemplateData { name = path.Substring("Assets/".Length) });
        }

        private static string ResolveMenuTemplate(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
                return null;
            var name = templateName.Trim().Trim('/');
            if (name.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("Assets/".Length);
            if (!name.StartsWith(MenuTemplatePrefix, StringComparison.OrdinalIgnoreCase))
                name = MenuTemplatePrefix + name;
            return MenuTemplates().Select(template => template.name).FirstOrDefault(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        }

        // Runs the menu item the way the Project window does; without a Project window Unity names the asset at once
        // instead of waiting for the rename field, then the asset is moved to the requested path.
        private static AssetData CreateFromMenu(string menuTemplate, string assetPath, string sourcePath, bool retryAfterTmpImport = true)
        {
            assetPath = NormalizeAssetPath(assetPath);
            // The source is what the developer selects first: a font, prefab or material, also from Packages.
            var source = string.IsNullOrWhiteSpace(sourcePath) ? null : AssetDatabase.LoadMainAssetAtPath(sourcePath.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(sourcePath) && source == null)
                throw new FileNotFoundException("Source asset was not found: " + sourcePath);
            var before = new HashSet<string>(AssetDatabase.GetAllAssetPaths(), StringComparer.Ordinal);
            var browser = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProjectBrowser", true)
                .GetField("s_LastInteractedProjectBrowser", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var lastBrowser = browser.GetValue(null);
            // Menu items explain a refusal only in the Console ("select a Font first", "import TMP Essentials").
            var messages = new List<string>();
            Application.LogCallback collect = (message, stack, type) => messages.Add(message.Split('\n')[0]);
            Application.logMessageReceived += collect;
            try
            {
                browser.SetValue(null, null);
                Selection.objects = source == null ? new UnityEngine.Object[0] : new[] { source };
                if (!EditorApplication.ExecuteMenuItem("Assets/" + menuTemplate))
                    throw new InvalidOperationException("Unity did not run " + menuTemplate + (source == null ? " (it may need --source <asset>)." : " for " + sourcePath + "."));
            }
            finally
            {
                browser.SetValue(null, lastBrowser);
                Application.logMessageReceived -= collect;
            }
            AssetDatabase.SaveAssets();
            var created = AssetDatabase.GetAllAssetPaths()
                .Where(path => !before.Contains(path) && path.StartsWith("Assets/", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(path))
                .ToList();
            // TMP refuses to create fonts before its essentials exist; the TMP Importer window's button is pressed and the item rerun.
            if (created.Count == 0 && retryAfterTmpImport && messages.Any(message => message.Contains("TMP Essential Resources")))
            {
                if (!TmpEssentialsService.Imported)
                {
                    TmpEssentialsService.Import(true);
                    AssetDatabase.Refresh();
                }
                if (!TmpEssentialsService.Imported)
                    throw new InvalidOperationException("TMP Essential Resources are being imported; repeat the command when Unity finishes.");
                var retry = CreateFromMenu(menuTemplate, assetPath, sourcePath, false);
                retry.changes = retry.changes.Concat(new[] { "Imported TMP Essential Resources into Assets/TextMesh Pro" }).ToArray();
                return retry;
            }
            if (created.Count == 0)
                throw new InvalidOperationException("Unity created no asset for " + menuTemplate + (messages.Count > 0 ? ". Console: " + string.Join(" | ", messages.Distinct())
                    : source == null ? "; it may need --source <asset>." : "."));
            var selected = Selection.activeObject == null ? null : AssetDatabase.GetAssetPath(Selection.activeObject);
            var main = created.Contains(selected) ? selected : created[0];
            var target = Path.ChangeExtension(assetPath, Path.GetExtension(main)).Replace('\\', '/');
            if (AssetDatabase.LoadMainAssetAtPath(target) != null)
            {
                foreach (var path in created)
                    AssetDatabase.DeleteAsset(path);
                throw new IOException("Asset already exists: " + target);
            }
            var folder = Path.GetDirectoryName(target).Replace('\\', '/');
            EnsureFolder(folder);
            var changes = new List<string>();
            foreach (var path in created)
            {
                // Companion assets carry the main asset's name, as when it is renamed in the Project window ("<name>_Renderer").
                var mainName = Path.GetFileNameWithoutExtension(main);
                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith(mainName, StringComparison.Ordinal))
                    fileName = Path.GetFileNameWithoutExtension(target) + fileName.Substring(mainName.Length);
                var destination = path == main ? target : AssetDatabase.GenerateUniqueAssetPath(folder + "/" + fileName);
                var error = AssetDatabase.MoveAsset(path, destination);
                if (!string.IsNullOrEmpty(error))
                    throw new InvalidOperationException(error);
                if (path != main)
                    changes.Add("Also created: " + destination);
            }
            AssetDatabase.SaveAssets();
            var info = GetInfo(target);
            info.changes = changes.ToArray();
            return info;
        }

        // Shaders and Shader Graphs under Assets that the Inspector shows with errors, for compile: path and first error.
        public static string GetShaderErrors()
        {
            var errors = new List<string>();
            foreach (var path in AssetDatabase.FindAssets("t:Shader", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Distinct())
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null || !ShaderUtil.ShaderHasError(shader))
                    continue;
                var first = ShaderUtil.GetShaderMessages(shader).FirstOrDefault(item => item.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
                errors.Add(path + ": " + (first.message ?? "error").Trim() + (first.line > 0 ? " (line " + first.line + ")" : string.Empty));
            }
            return new JsonText().Add("shaders", errors.ToArray()).ToString();
        }

        // Global shader values set with Shader.SetGlobal*, by name: a texture as its asset, a vector as x,y,z,w, a float.
        public static string GetGlobalShaderProperties(string names)
        {
            var result = new JsonText();
            foreach (var name in (names ?? string.Empty).Split(',').Select(item => item.Trim()).Where(item => item.Length > 0))
            {
                var id = Shader.PropertyToID(name);
                var texture = Shader.GetGlobalTexture(id);
                var vector = Shader.GetGlobalVector(id);
                var number = Shader.GetGlobalFloat(id);
                if (texture != null)
                    result.Add(name, AssetViews.ObjectLabel(texture));
                // Unity keeps global floats and vectors apart; the one that was set is non-zero.
                else if (vector != Vector4.zero)
                    result.Add(name, AssetViews.Numbers(vector.x, vector.y, vector.z, vector.w));
                else
                    result.Add(name, Math.Round(number, 6));
            }
            if (result.Count == 0)
                throw new ArgumentException("Name the global properties: --property _Name[,_Other].", "propertyPath");
            return result.ToString();
        }

        public static string GetShaderInfo(string assetPath, string propertyName = null)
        {
            return ShaderInfo(assetPath, string.IsNullOrWhiteSpace(propertyName) ? null : propertyName.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToArray());
        }

        // Without names a material lists the values it changed and only the names of those left at the shader's
        // defaults; a shader lists its defaults. Named properties are shown whatever their value.
        private static string ShaderInfo(string assetPath, string[] names)
        {
            assetPath = ValidateExistingAssetPath(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            var material = asset as Material;
            var shader = asset as Shader ?? (material == null ? null : material.shader);
            if (shader == null)
                throw new InvalidOperationException("Shader info requires a Material or Shader asset.");

            var temporaryMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var source = material ?? temporaryMaterial;
            try
            {
                var own = material == null ? new HashSet<string>() : OwnHiddenValues(shader, material);
                var named = names == null ? null : new HashSet<string>(names.Select(name => ShaderPropertyName(shader, name) ?? name), StringComparer.OrdinalIgnoreCase);
                var indices = Enumerable.Range(0, shader.GetPropertyCount())
                    .Where(index => named != null ? named.Contains(shader.GetPropertyName(index)) :
                        (shader.GetPropertyFlags(index) & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) == 0 ||
                        own.Contains(shader.GetPropertyName(index)))
                    .ToArray();
                if (named != null && indices.Length < named.Count)
                    throw new InvalidOperationException("Shader property was not found: " + string.Join(", ",
                        named.Where(name => !indices.Any(index => string.Equals(shader.GetPropertyName(index), name, StringComparison.OrdinalIgnoreCase))).ToArray()));
                var properties = indices.Select(index => ShaderProperty(shader, source, index)).ToArray();
                var defaults = Array.Empty<string>();
                if (named == null && material != null)
                {
                    var atDefault = new HashSet<string>(indices.Select(index => ShaderProperty(shader, temporaryMaterial, index))
                        .Where((item, position) => item.value == properties[position].value).Select(item => item.name));
                    defaults = properties.Where(item => atDefault.Contains(item.name)).Select(item => item.name).ToArray();
                    properties = properties.Where(item => !atDefault.Contains(item.name)).ToArray();
                }
                var errors = ShaderUtil.GetShaderMessages(shader)
                    .Select(item => new ShaderErrorData
                    {
                        severity = item.severity.ToString(),
                        message = item.message,
                        file = item.file,
                        line = item.line
                    })
                    .ToArray();
                // Variant errors (HDRP, Shader Graph) often reach only the Console; the shader still knows it failed.
                if (!errors.Any(item => item.severity == "Error") && ShaderUtil.ShaderHasError(shader))
                    errors = errors.Concat(new[] { new ShaderErrorData
                    {
                        severity = "Error",
                        message = "The shader failed to compile; the errors are in the Console: logs --query \"" + shader.name + "\""
                    } }).ToArray();
                EditorPresentationService.ShowAsset(asset);
                return JsonUtility.ToJson(new ShaderInfoData
                {
                    name = asset.name,
                    path = assetPath,
                    shader = shader.name,
                    shaderPath = AssetDatabase.GetAssetPath(shader),
                    properties = properties,
                    defaults = defaults,
                    errors = errors
                });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temporaryMaterial);
            }
        }

        public static string ModifyMaterial(string assetPath, PropertyValue[] values)
        {
            assetPath = ValidateExistingAssetPath(assetPath);
            if (values == null || values.Length == 0)
                throw new InvalidOperationException("Material modification requires at least one property value.");
            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
                throw new InvalidOperationException("Material asset could not be loaded: " + assetPath);
            var shaderEntry = values.FirstOrDefault(entry => entry != null && AssetViews.KeyIs(entry.path, "Shader"));
            if (shaderEntry != null)
            {
                AssignShader(material, AssetViews.Text(shaderEntry.value));
                values = values.Where(entry => entry != shaderEntry).ToArray();
                if (values.Length == 0)
                {
                    EditorPresentationService.ShowAsset(material);
                    return GetShaderInfo(assetPath, null);
                }
            }
            var names = SetMaterialValues(material, values);
            EditorPresentationService.ShowAsset(material);
            return ShaderInfo(assetPath, names);
        }

        private static string[] SetMaterialValues(Material material, PropertyValue[] values)
        {
            var shader = material.shader;
            var names = new List<string>();
            var preview = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                foreach (var entry in values)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.path))
                        throw new InvalidOperationException("Every material value requires a shader property name.");
                    var name = ShaderPropertyName(shader, entry.path);
                    if (name == null)
                        throw new InvalidOperationException("Shader property was not found: " + entry.path);
                    SetShaderProperty(preview, name, shader.GetPropertyType(shader.FindPropertyIndex(name)).ToString(), entry.value);
                    names.Add(name);
                }
                Undo.RecordObject(material, "Unity Agent Bridge: Modify Material");
                material.CopyPropertiesFromMaterial(preview);
                ValidateMaterial(material);
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
                return names.ToArray();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(preview);
            }
        }

        // A shader property by its name or by its label in the material Inspector (`Light Full` for `_Light_Full`).
        private static string ShaderPropertyName(Shader shader, string key)
        {
            key = key.Trim();
            var indices = Enumerable.Range(0, shader.GetPropertyCount()).ToArray();
            var named = indices.Select(index => shader.GetPropertyName(index)).FirstOrDefault(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));
            if (named != null)
                return named;
            // Labels are compared without spaces and underscores: HDRP Lit names Base Color "BaseColor" and Metallic "_Metallic".
            Func<string, string> bare = text => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            var wanted = bare(key);
            var labelled = indices.Where(index => bare(shader.GetPropertyDescription(index)) == wanted || bare(shader.GetPropertyName(index)) == wanted)
                .Select(index => shader.GetPropertyName(index)).ToArray();
            if (labelled.Length > 1)
                throw new InvalidOperationException("Several shader properties are labelled " + key + ": " + string.Join(", ", labelled) + ".");
            if (labelled.Length == 1)
                return labelled[0];
            // HDRP draws Surface Options with its own labels, not the shader's descriptions.
            string surfaceOption;
            return HdrpSurfaceOptions.TryGetValue(wanted, out surfaceOption) && shader.FindPropertyIndex(surfaceOption) >= 0 ? surfaceOption : null;
        }

        private static readonly Dictionary<string, string> HdrpSurfaceOptions = new Dictionary<string, string>
        {
            { "surfacetype", "_SurfaceType" }, { "renderingpass", "_RenderQueueType" }, { "blendingmode", "_BlendMode" },
            { "sortingpriority", "_TransparentSortPriority" }, { "doublesided", "_DoubleSidedEnable" }, { "alphaclipping", "_AlphaCutoffEnable" },
            { "receivedecals", "_SupportDecals" }, { "receivessr", "_ReceivesSSR" }, { "depthwrite", "_TransparentZWrite" },
            { "depthtest", "_ZTestTransparent" }
        };

        // The Inspector's Shader popup: the shader's GUI sets up its keywords and render states for the new shader.
        private static void AssignShader(Material material, string raw)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(raw) ?? Shader.Find(raw);
            if (shader == null)
                throw new InvalidOperationException("Shader was not found: " + raw);
            var editor = (MaterialEditor)UnityEditor.Editor.CreateEditor(material);
            try
            {
                editor.SetShader(shader, true);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
            }
            ValidateMaterial(material);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
        }

        // Hidden numbers the material holds apart from its shader, such as HDRP Surface Options a graph only defaults.
        // Values the shader GUI derives from others (blend and stencil states) come back after validation and are left out.
        private static HashSet<string> OwnHiddenValues(Shader shader, Material material)
        {
            var differing = Enumerable.Range(0, shader.GetPropertyCount())
                .Where(index => (shader.GetPropertyFlags(index) & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) != 0 &&
                    (shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Float || shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Range) &&
                    material.GetFloat(shader.GetPropertyName(index)) != shader.GetPropertyDefaultFloatValue(index))
                .ToDictionary(index => shader.GetPropertyName(index), index => shader.GetPropertyDefaultFloatValue(index));
            if (differing.Count == 0)
                return new HashSet<string>();
            var probe = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                foreach (var item in differing)
                    probe.SetFloat(item.Key, item.Value);
                ValidateMaterial(probe);
                return new HashSet<string>(differing.Keys.Where(name => probe.GetFloat(name) != material.GetFloat(name)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        internal static Dictionary<string, float> FloatDefaults(string shaderPath)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            if (shader == null)
                return result;
            for (var index = 0; index < shader.GetPropertyCount(); index++)
                if (shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Float || shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Range)
                    result[shader.GetPropertyName(index)] = shader.GetPropertyDefaultFloatValue(index);
            return result;
        }

        // HDRP materials keep their own copy of Surface Options (Surface Type, Refraction Model...); the graph only sets
        // the defaults of new materials. Materials that still hold a changed default are named, as the Inspector would show them.
        internal static List<string> MaterialsKeeping(string shaderPath, Dictionary<string, float> before)
        {
            var after = FloatDefaults(shaderPath);
            var changed = after.Where(item => before.ContainsKey(item.Key) && before[item.Key] != item.Value).Select(item => item.Key).ToList();
            var result = new List<string>();
            if (changed.Count == 0)
                return result;
            foreach (var materialPath in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(item => item.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) && AssetDatabase.GetDependencies(item, false).Contains(shaderPath)))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                var kept = changed.Where(name => material != null && material.HasProperty(name) && material.GetFloat(name) != after[name])
                    .Select(name => name + "=" + AssetViews.Numbers(material.GetFloat(name)) + " (graph " + AssetViews.Numbers(after[name]) + ")").ToList();
                if (kept.Count > 0)
                    result.Add(materialPath + " keeps " + string.Join(", ", kept));
            }
            return result;
        }

        // What the Inspector does after an edit: the shader's GUI updates keywords, render queue and states.
        private static void ValidateMaterial(Material material)
        {
            SyncHdrpRenderQueue(material);
            var editor = (MaterialEditor)UnityEditor.Editor.CreateEditor(material);
            try
            {
                var gui = typeof(MaterialEditor).GetProperty("customShaderGUI", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(editor) as ShaderGUI;
                if (gui != null)
                    gui.ValidateMaterial(material);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
            }
        }

        // The HDRP Inspector sets the render queue from the Rendering Pass a Shader Graph material keeps in _RenderQueueType and
        // from Sorting Priority when it draws Surface Options, not in ValidateMaterial; the same is done here.
        private static void SyncHdrpRenderQueue(Material material)
        {
            var queue = Type.GetType("UnityEngine.Rendering.HighDefinition.HDRenderQueue, Unity.RenderPipelines.HighDefinition.Runtime");
            if (queue == null || !material.HasProperty("_RenderQueueType"))
                return;
            var wanted = Enum.ToObject(queue.GetNestedType("RenderQueueType"), (int)material.GetFloat("_RenderQueueType"));
            Func<string, float> number = name => material.HasProperty(name) ? material.GetFloat(name) : 0f;
            var transparent = number("_SurfaceType") > 0f;
            var value = (int)queue.GetMethod("ChangeType").Invoke(null, new object[]
                { wanted, transparent ? (int)number("_TransparentSortPriority") : 0, !transparent && number("_AlphaCutoffEnable") > 0f, !transparent && number("_SupportDecals") > 0f });
            if (material.renderQueue != value)
                material.renderQueue = value;
        }

        private static ShaderPropertyInfoData ShaderProperty(Shader shader, Material material, int index)
        {
            var name = shader.GetPropertyName(index);
            var type = shader.GetPropertyType(index).ToString();
            string value;
            switch (type)
            {
                case "Color":
                    var color = material.GetColor(name);
                    value = AssetViews.Numbers(color.r, color.g, color.b, color.a);
                    break;
                case "Vector":
                    var vector = material.GetVector(name);
                    value = AssetViews.Numbers(vector.x, vector.y, vector.z, vector.w);
                    break;
                case "Float":
                case "Range":
                    value = AssetViews.Numbers(material.GetFloat(name));
                    break;
                case "Int":
                    value = material.GetInt(name).ToString(CultureInfo.InvariantCulture);
                    break;
                case "TexEnv":
                case "Texture":
                    var texture = material.GetTexture(name);
                    value = texture == null ? string.Empty : AssetDatabase.GetAssetPath(texture);
                    if (texture != null && string.IsNullOrEmpty(value))
                        value = texture.name;
                    break;
                default:
                    value = string.Empty;
                    break;
            }
            return new ShaderPropertyInfoData { name = name, type = type, value = value };
        }

        private static void SetShaderProperty(Material material, string name, string type, string rawValue)
        {
            switch (type)
            {
                case "Color":
                    var color = ParseShaderColor(rawValue);
                    material.SetColor(name, new Color(color.r, color.g, color.b, color.a));
                    return;
                case "Vector":
                    var vector = ParseShaderVector(rawValue);
                    material.SetVector(name, new Vector4(vector.x, vector.y, vector.z, vector.w));
                    return;
                case "Float":
                case "Range":
                    material.SetFloat(name, float.Parse(DecodeJsonString(rawValue), NumberStyles.Float, CultureInfo.InvariantCulture));
                    return;
                case "Int":
                    material.SetInt(name, int.Parse(DecodeJsonString(rawValue), NumberStyles.Integer, CultureInfo.InvariantCulture));
                    return;
                case "TexEnv":
                case "Texture":
                    var path = DecodeJsonString(rawValue);
                    var texture = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.LoadAssetAtPath<Texture>(path);
                    if (!string.IsNullOrWhiteSpace(path) && texture == null)
                        throw new InvalidOperationException("Texture asset was not found: " + path);
                    material.SetTexture(name, texture);
                    return;
                default:
                    throw new NotSupportedException("Shader property type is not supported: " + type);
            }
        }

        private static ShaderColorData ParseShaderColor(string rawValue)
        {
            if (!string.IsNullOrWhiteSpace(rawValue) && rawValue.TrimStart().StartsWith("{", StringComparison.Ordinal))
                return JsonUtility.FromJson<ShaderColorData>(rawValue);
            var values = NumberParts(DecodeJsonString(rawValue), 4, "Color");
            return new ShaderColorData { r = values[0], g = values[1], b = values[2], a = values[3] };
        }

        private static ShaderVectorData ParseShaderVector(string rawValue)
        {
            if (!string.IsNullOrWhiteSpace(rawValue) && rawValue.TrimStart().StartsWith("{", StringComparison.Ordinal))
                return JsonUtility.FromJson<ShaderVectorData>(rawValue);
            var values = NumberParts(DecodeJsonString(rawValue), 4, "Vector");
            return new ShaderVectorData { x = values[0], y = values[1], z = values[2], w = values[3] };
        }

        private static float[] NumberParts(string value, int count, string label)
        {
            var parts = (value ?? string.Empty).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != count)
                throw new FormatException(label + " requires " + count + " numbers.");
            return parts.Select(part => float.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        }

        private static string DecodeJsonString(string rawValue)
        {
            if (rawValue == null)
                return string.Empty;
            var value = rawValue.Trim();
            if (!value.StartsWith("\"", StringComparison.Ordinal))
                return value;
            var parsed = JsonUtility.FromJson<JsonStringData>("{\"value\":" + value + "}");
            return parsed == null ? string.Empty : parsed.value;
        }

        public static string ImportPackage(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Unity package path is required.", "path");
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !fullPath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Unity package was not found.", fullPath);
            AssetDatabase.ImportPackage(fullPath, false);
            return "Unity package imported.";
        }

        public static AssetData Create(string templateName, string assetPath, string sourcePath = null)
        {
            var template = BuiltInTemplates().SingleOrDefault(item => string.Equals(item.name, templateName, StringComparison.OrdinalIgnoreCase));
            var menuTemplate = template == null ? ResolveMenuTemplate(templateName) : null;
            if (menuTemplate != null)
                return CreateFromMenu(menuTemplate, assetPath, sourcePath);
            var scriptableObjectType = template == null ? ResolveScriptableObjectTemplateType(templateName) : null;
            if (template == null && scriptableObjectType == null)
            {
                var source = PackageTemplates.FirstOrDefault(item => item.list().Any(entry => string.Equals(entry.name, templateName, StringComparison.OrdinalIgnoreCase)));
                if (source == null)
                    throw new InvalidOperationException("Creation Template was not found: " + templateName);
                var entryExtension = source.list().First(entry => string.Equals(entry.name, templateName, StringComparison.OrdinalIgnoreCase)).extension;
                assetPath = ValidateNewAssetPath(assetPath, entryExtension);
                EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
                source.create(templateName, assetPath);
                return GetInfo(assetPath);
            }
            assetPath = ValidateNewAssetPath(assetPath, scriptableObjectType == null ? template.extension : ScriptableObjectExtension(scriptableObjectType));
            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));

            if (scriptableObjectType != null)
            {
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance(scriptableObjectType), assetPath);
                AssetDatabase.SaveAssets();
                return GetInfo(assetPath);
            }

            switch (template.name)
            {
                case "C# Script":
                    WriteText(assetPath, "using UnityEngine;\n\npublic sealed class " + ClassName(assetPath) + " : MonoBehaviour\n{\n}\n");
                    break;
                case "C# Editor Script":
                    WriteText(assetPath, "using UnityEditor;\nusing UnityEngine;\n\npublic sealed class " + ClassName(assetPath) + " : EditorWindow\n{\n}\n");
                    break;
                case "ScriptableObject Script":
                    WriteText(assetPath, "using UnityEngine;\n\n[CreateAssetMenu]\npublic sealed class " + ClassName(assetPath) + " : ScriptableObject\n{\n}\n");
                    break;
                case "Text File":
                    WriteText(assetPath, string.Empty);
                    break;
                case "JSON File":
                    WriteText(assetPath, "{}\n");
                    break;
                case "Shader":
                    WriteText(assetPath, "Shader \"Custom/" + ClassName(assetPath) + "\"\n{\n    SubShader { Pass { } }\n}\n");
                    break;
                case "Compute Shader":
                    WriteText(assetPath, "#pragma kernel CSMain\n\n[numthreads(8, 8, 1)]\nvoid CSMain(uint3 id : SV_DispatchThreadID)\n{\n}\n");
                    break;
                case "Animator Controller":
                    AnimationEditorWindowService.Open(AnimatorController.CreateAnimatorControllerAtPath(assetPath));
                    break;
                case "Animation Clip":
                    AssetDatabase.CreateAsset(new AnimationClip(), assetPath);
                    break;
                case "Material":
                    // As Create > Material with a shader selected: the new material starts from that shader's defaults.
                    var pipeline = GraphicsSettings.currentRenderPipeline;
                    var selected = string.IsNullOrWhiteSpace(sourcePath) ? null : AssetDatabase.LoadAssetAtPath<Shader>(sourcePath.Replace('\\', '/'));
                    if (!string.IsNullOrWhiteSpace(sourcePath) && selected == null)
                        throw new FileNotFoundException("Source shader was not found: " + sourcePath);
                    var shader = selected ?? (pipeline != null ? pipeline.defaultShader : Shader.Find("Standard"));
                    if (shader == null)
                        throw new InvalidOperationException("Default shader was not found.");
                    var material = new Material(shader);
                    AssetDatabase.CreateAsset(material, assetPath);
                    ValidateMaterial(material);
                    break;
                case "Timeline":
                    var timeline = ScriptableObject.CreateInstance(TimelineAssetType);
                    AssetDatabase.CreateAsset(timeline, assetPath);
                    AssetDatabase.OpenAsset(timeline);
                    break;
                case "Scene":
                    var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    EditorSceneManager.SaveScene(scene, assetPath);
                    EditorSceneManager.CloseScene(scene, true);
                    break;
                default:
                    throw new InvalidOperationException("Creation Template is not implemented: " + template.name);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.SaveAssets();
            return GetInfo(assetPath);
        }

        private static IEnumerable<CreationTemplateData> ScriptableObjectTemplates()
        {
            return TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                .Where(IsCreatableScriptableObjectType)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Select(type => new CreationTemplateData
                {
                    name = ScriptableObjectTemplatePrefix + type.FullName,
                    extension = ScriptableObjectExtension(type)
                });
        }

        // Unity saves GUI skins only as .guiskin.
        private static string ScriptableObjectExtension(Type type)
        {
            return typeof(GUISkin).IsAssignableFrom(type) ? ".guiskin" : ".asset";
        }

        private static Type ResolveScriptableObjectTemplateType(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName)
                || !templateName.StartsWith(ScriptableObjectTemplatePrefix, StringComparison.OrdinalIgnoreCase))
                return null;
            var typeName = templateName.Substring(ScriptableObjectTemplatePrefix.Length);
            return TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                .SingleOrDefault(type => IsCreatableScriptableObjectType(type)
                    && string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCreatableScriptableObjectType(Type type)
        {
            return type != null
                && !type.IsAbstract
                && !type.IsGenericTypeDefinition
                && (type.IsPublic || type.IsNestedPublic)
                && !typeof(UnityEditor.Editor).IsAssignableFrom(type)
                && !typeof(EditorWindow).IsAssignableFrom(type)
                && !typeof(StateMachineBehaviour).IsAssignableFrom(type)
                && !IsScriptableSingleton(type)
                && !CreatedByItsOwnMenu(type)
                && !type.IsDefined(typeof(CreateAssetMenuAttribute), false)
                && (type.Namespace == null || !type.Namespace.StartsWith("UnityEditor", StringComparison.Ordinal)
                    && type.Namespace != "UnityEngine.Timeline");
        }

        // Sub-assets and assets whose Create menu item builds more than an empty instance (fonts, panel settings, overrides).
        private static readonly string[] MenuOnlyTypes =
        {
            "UnityEngine.Rendering.VolumeComponent", "UnityEngine.Rendering.Universal.ScriptableRendererFeature",
            "TMPro.TMP_FontAsset", "UnityEngine.TextCore.Text.FontAsset", "UnityEngine.UIElements.PanelSettings"
        };

        private static bool CreatedByItsOwnMenu(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (MenuOnlyTypes.Contains(current.FullName))
                    return true;
            return false;
        }

        private static bool IsScriptableSingleton(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ScriptableSingleton<>))
                    return true;
            return false;
        }

        public static AssetData GetInfo(string assetPath, string propertyPath = null, string section = null)
        {
            if (AssetViews.IsVirtualPath(assetPath))
            {
                var settings = AssetViews.Find(assetPath, null);
                return new AssetData
                {
                    name = assetPath.Substring(assetPath.LastIndexOf('/') + 1),
                    assetPath = assetPath,
                    type = settings is ProjectSettingsView ? "Project Settings" : "Window",
                    properties = Array.Empty<SerializedPropertyData>(),
                    actions = ViewActions(settings, assetPath, null),
                    view = settings.Describe(assetPath, null, propertyPath).ToString()
                };
            }
            string subAsset;
            assetPath = ValidateExistingAssetPath(SplitSubAsset(assetPath, out subAsset));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (main == null)
                throw new InvalidOperationException("Asset could not be loaded: " + assetPath);
            var asset = subAsset == null ? main : FindSubAsset(assetPath, subAsset);
            var view = AssetViews.Find(assetPath, asset);
            if (view == null || !view.OwnsWindow)
                EditorPresentationService.ShowAsset(main);
            var importer = subAsset == null ? AssetImporter.GetAtPath(assetPath) : null;
            var properties = new List<SerializedPropertyData>();
            if (!string.IsNullOrWhiteSpace(section) && !string.Equals(section, "model", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Asset info section must be model.", "section");
            if (string.Equals(section, "model", StringComparison.OrdinalIgnoreCase) && !(importer is ModelImporter))
                throw new InvalidOperationException("Model section requires an imported model asset.");
            var viewJson = view == null ? null : view.Describe(assetPath, asset, propertyPath).ToString();
            var generic = view == null || view.KeepsProperties;
            if (generic && string.IsNullOrWhiteSpace(section) && string.IsNullOrWhiteSpace(propertyPath))
            {
                AddProperties(asset, "asset", properties);
                if (importer != null)
                    AddProperties(importer, "importer", properties);
                var script = asset as MonoScript;
                if (script != null && script.GetClass() != null)
                    AddScriptFields(script.GetClass(), properties);
            }
            else if (generic && string.IsNullOrWhiteSpace(section))
            {
                AddSelectedProperties(asset, importer, propertyPath, properties);
            }

            return new AssetData
            {
                name = asset.name,
                assetPath = subAsset == null ? assetPath : assetPath + "#" + subAsset,
                type = asset.GetType().FullName,
                importerType = importer == null || importer.GetType() == typeof(AssetImporter) ? string.Empty : importer.GetType().FullName,
                properties = properties.ToArray(),
                actions = ViewActions(view, assetPath, asset),
                view = viewJson,
                subAssets = subAsset == null && generic ? SubAssetNames(assetPath, main) : null,
                model = string.Equals(section, "model", StringComparison.OrdinalIgnoreCase)
                    ? DescribeModel(assetPath, (ModelImporter)importer)
                    : null
            };
        }

        private static InspectorActionData[] ViewActions(AssetView view, string path, UnityEngine.Object asset)
        {
            return view == null
                ? Array.Empty<InspectorActionData>()
                : view.Actions(path, asset).Select(label => new InspectorActionData { id = AssetViews.ActionId(label), label = label }).ToArray();
        }

        // "Assets/x.asset#Name" addresses a sub-asset by name, type name or the picker's "Type[index]" form.
        private static string SplitSubAsset(string path, out string subAsset)
        {
            var separator = path == null ? -1 : path.IndexOf('#');
            subAsset = separator < 0 ? null : Uri.UnescapeDataString(path.Substring(separator + 1));
            return separator < 0 ? path : path.Substring(0, separator);
        }

        internal static UnityEngine.Object FindSubAsset(string assetPath, string key)
        {
            var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath).Where(item => item != null && !AssetDatabase.IsMainAsset(item)).ToArray();
            var indexed = Regex.Match(key, @"^(.+)\[(\d+)\]$");
            if (indexed.Success)
            {
                var typed = subAssets.Where(item => item.GetType().FullName == indexed.Groups[1].Value || item.GetType().Name == indexed.Groups[1].Value).ToArray();
                var index = int.Parse(indexed.Groups[2].Value, CultureInfo.InvariantCulture);
                if (index < typed.Length)
                    return typed[index];
            }
            var matches = subAssets.Where(item => item.name == key).ToArray();
            if (matches.Length == 0)
                matches = subAssets.Where(item => AssetViews.Normalize(item.name) == AssetViews.Normalize(key) ||
                    AssetViews.Normalize(item.GetType().Name) == AssetViews.Normalize(key)).ToArray();
            if (matches.Length == 1)
                return matches[0];
            throw new InvalidOperationException(matches.Length == 0
                ? "Sub-asset was not found: " + key + ". Sub-assets: " + string.Join(", ", SubAssetNames(assetPath, null) ?? Array.Empty<string>())
                : "Sub-asset name is ambiguous; use <Type>[index]: " + key);
        }

        private static string[] SubAssetNames(string assetPath, UnityEngine.Object main)
        {
            if (main is SceneAsset || AssetDatabase.IsValidFolder(assetPath))
                return null;
            var names = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath)
                .Where(item => item != null)
                .Select(item => item.name + " (" + item.GetType().Name + ")")
                .Take(30)
                .ToArray();
            return names.Length == 0 ? null : names;
        }

        private static ModelInfoData DescribeModel(string assetPath, ModelImporter importer)
        {
            return new ModelInfoData
            {
                animationType = importer.animationType.ToString(),
                avatarSetup = importer.avatarSetup.ToString(),
                importAnimation = importer.importAnimation,
                meshes = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                    .OfType<Mesh>()
                    .OrderBy(mesh => mesh.name, StringComparer.Ordinal)
                    .Select(mesh => new MeshInfoData
                    {
                        name = mesh.name,
                        vertexCount = mesh.vertexCount,
                        subMeshCount = mesh.subMeshCount,
                        boundsCenter = new[] { mesh.bounds.center.x, mesh.bounds.center.y, mesh.bounds.center.z },
                        boundsSize = new[] { mesh.bounds.size.x, mesh.bounds.size.y, mesh.bounds.size.z }
                    })
                    .ToArray()
            };
        }

        public static string ExecuteAction(string assetPath, string action, PropertyValue[] values = null)
        {
            if (AssetViews.IsVirtualPath(assetPath))
                return AssetViews.Find(assetPath, null).Execute(assetPath, null, action, values);
            string subAsset;
            assetPath = ValidateExistingAssetPath(SplitSubAsset(assetPath, out subAsset));
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                throw new InvalidOperationException("Asset could not be loaded: " + assetPath);
            if (subAsset != null)
                asset = FindSubAsset(assetPath, subAsset);
            var view = AssetViews.Find(assetPath, asset);
            if (view != null && view.Actions(assetPath, asset).Any(label =>
                    AssetViews.Normalize(label) == AssetViews.Normalize(action)))
            {
                if (!view.OwnsWindow)
                    EditorPresentationService.ShowAssetPath(assetPath);
                return view.Execute(assetPath, asset, action, values);
            }
            var actions = view == null ? Array.Empty<string>() : view.Actions(assetPath, asset);
            throw new InvalidOperationException("Unknown asset action: " + action + ". " +
                (actions.Length > 0 ? "Available: " + string.Join(", ", actions) + ". " : string.Empty) + "Values are set with asset-modify --set.");
        }

        public static AssetData Reimport(string assetPath, bool asSprite)
        {
            assetPath = NormalizeAssetPath(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            assetPath = ValidateExistingAssetPath(assetPath);
            if (asSprite)
            {
                var textureImporter = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (textureImporter == null)
                    throw new InvalidOperationException("Sprite import requires a texture asset: " + assetPath);
                textureImporter.textureType = TextureImporterType.Sprite;
                textureImporter.spriteImportMode = SpriteImportMode.Single;
                textureImporter.SaveAndReimport();
                return GetInfo(assetPath);
            }
            return GetInfo(assetPath);
        }

        public static string GetSpriteLayout(string assetPath)
        {
            assetPath = ValidateExistingAssetPath(assetPath);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                throw new InvalidOperationException("Sprite editor requires a texture asset: " + assetPath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null)
                throw new InvalidOperationException("Texture could not be loaded: " + assetPath);
            EditorPresentationService.ShowAsset(texture);
            return JsonUtility.ToJson(BuildSpriteLayout(assetPath, texture, importer));
        }

        public static string MutateSpriteLayout(string assetPath, string action, string json)
        {
            assetPath = ValidateExistingAssetPath(assetPath);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                throw new InvalidOperationException("Sprite editor requires a texture asset: " + assetPath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null)
                throw new InvalidOperationException("Texture could not be loaded: " + assetPath);

            var payload = string.IsNullOrWhiteSpace(json)
                ? new SpriteMutationPayload()
                : JsonUtility.FromJson<SpriteMutationPayload>(json);
            if (payload == null)
                throw new ArgumentException("Sprite editor payload is invalid.", "json");

            switch (action)
            {
                case "auto":
                    ApplyMultipleSpriteLayout(assetPath, importer, AutomaticSpriteSlices(texture));
                    break;
                case "manual":
                    ApplyMultipleSpriteLayout(assetPath, importer, ValidateSpriteSlices(payload.slices, texture.width, texture.height));
                    break;
                case "border":
                    if (payload.border == null)
                        throw new ArgumentException("border action requires border.", "json");
                    ValidateBorder(payload.border, texture.width, texture.height);
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.spriteBorder = new Vector4(
                        payload.border.left,
                        payload.border.bottom,
                        payload.border.right,
                        payload.border.top);
                    importer.SaveAndReimport();
                    break;
                default:
                    throw new ArgumentException("Sprite editor action must be auto, manual or border.", "action");
            }

            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            EditorPresentationService.ShowAsset(texture);
            return JsonUtility.ToJson(BuildSpriteLayout(assetPath, texture, importer));
        }

        private static SpriteSliceInput[] AutomaticSpriteSlices(Texture2D texture)
        {
            var utility = typeof(EditorApplication).Assembly.GetType("UnityEditorInternal.InternalSpriteUtility", true);
            var method = utility.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "GenerateAutomaticSpriteRectangles")
                .Single(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length >= 2
                        && parameters[0].ParameterType == typeof(Texture2D)
                        && parameters.Skip(1).All(parameter => parameter.ParameterType == typeof(int));
                });
            var arguments = new object[method.GetParameters().Length];
            arguments[0] = texture;
            for (var index = 1; index < arguments.Length; index++)
                arguments[index] = index == 1 ? 4 : 0;
            var rectangles = method.Invoke(null, arguments) as Rect[];
            if (rectangles == null || rectangles.Length == 0)
                throw new InvalidOperationException("Unity automatic sprite slicing found no sprites.");
            return rectangles
                .OrderByDescending(rectangle => rectangle.y)
                .ThenBy(rectangle => rectangle.x)
                .Select((rectangle, index) => new SpriteSliceInput
                {
                    name = texture.name + "_" + index.ToString(CultureInfo.InvariantCulture),
                    x = rectangle.x,
                    y = rectangle.y,
                    width = rectangle.width,
                    height = rectangle.height
                })
                .ToArray();
        }

        private static SpriteSliceInput[] ValidateSpriteSlices(SpriteSliceInput[] slices, int width, int height)
        {
            if (slices == null || slices.Length == 0)
                throw new ArgumentException("manual action requires at least one slice.", "json");
            var names = new HashSet<string>(StringComparer.Ordinal);
            return slices.Select((slice, index) =>
            {
                if (slice == null || slice.width <= 0 || slice.height <= 0 || slice.x < 0 || slice.y < 0
                    || slice.x + slice.width > width || slice.y + slice.height > height)
                    throw new ArgumentException("Sprite slice " + index + " is outside the texture or has an invalid size.", "json");
                var name = string.IsNullOrWhiteSpace(slice.name)
                    ? "slice_" + index.ToString(CultureInfo.InvariantCulture)
                    : slice.name.Trim();
                if (!names.Add(name))
                    throw new ArgumentException("Sprite slice names must be unique: " + name, "json");
                return new SpriteSliceInput
                {
                    name = name,
                    x = slice.x,
                    y = slice.y,
                    width = slice.width,
                    height = slice.height
                };
            }).ToArray();
        }

        private static void ApplyMultipleSpriteLayout(string assetPath, TextureImporter importer, SpriteSliceInput[] slices)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
#if UNITY_6000_0_OR_NEWER
            importer.SaveAndReimport();
            ApplySpriteDataProviderLayout(assetPath, slices);
#else
#pragma warning disable 618
            importer.spritesheet = slices.Select(slice => new SpriteMetaData
            {
                name = slice.name,
                rect = new Rect(slice.x, slice.y, slice.width, slice.height),
                alignment = (int)SpriteAlignment.Center,
                pivot = new Vector2(0.5f, 0.5f),
                border = Vector4.zero
            }).ToArray();
#pragma warning restore 618
            importer.SaveAndReimport();
#endif
        }

#if UNITY_6000_0_OR_NEWER
        private static void ApplySpriteDataProviderLayout(string assetPath, SpriteSliceInput[] slices)
        {
            var factoryType = FindSpriteEditorType("UnityEditor.U2D.Sprites.SpriteDataProviderFactories");
            var providerType = FindSpriteEditorType("UnityEditor.U2D.Sprites.ISpriteEditorDataProvider");
            var nameProviderType = FindSpriteEditorType("UnityEditor.U2D.Sprites.ISpriteNameFileIdDataProvider");
            var spriteRectType = FindSpriteEditorType("UnityEditor.SpriteRect");
            var pairType = FindSpriteEditorType("UnityEditor.SpriteNameFileIdPair");
            var importer = AssetImporter.GetAtPath(assetPath);
            var factories = Activator.CreateInstance(factoryType);
            factoryType.GetMethod("Init").Invoke(factories, null);
            var provider = factoryType.GetMethod("GetSpriteEditorDataProviderFromObject")
                .Invoke(factories, new object[] { importer });
            if (provider == null)
                throw new InvalidOperationException("Unity Sprite Editor cannot edit this texture.");
            providerType.GetMethod("InitSpriteEditorDataProvider").Invoke(provider, null);

            var rects = Array.CreateInstance(spriteRectType, slices.Length);
            var pairs = Array.CreateInstance(pairType, slices.Length);
            var spriteId = spriteRectType.GetProperty("spriteID");
            var guidType = spriteId.PropertyType;
            var generateGuid = guidType.GetMethod("Generate", BindingFlags.Static | BindingFlags.Public);
            for (var index = 0; index < slices.Length; index++)
            {
                var slice = slices[index];
                var guid = generateGuid.Invoke(null, null);
                var rect = Activator.CreateInstance(spriteRectType);
                spriteRectType.GetProperty("name").SetValue(rect, slice.name, null);
                spriteRectType.GetProperty("rect").SetValue(rect, new Rect(slice.x, slice.y, slice.width, slice.height), null);
                spriteRectType.GetProperty("pivot").SetValue(rect, new Vector2(0.5f, 0.5f), null);
                spriteRectType.GetProperty("alignment").SetValue(rect, SpriteAlignment.Center, null);
                spriteRectType.GetProperty("border").SetValue(rect, Vector4.zero, null);
                spriteId.SetValue(rect, guid, null);
                rects.SetValue(rect, index);
                pairs.SetValue(Activator.CreateInstance(pairType, new[] { slice.name, guid }), index);
            }

            providerType.GetMethod("SetSpriteRects").Invoke(provider, new object[] { rects });
            var getDataProvider = providerType.GetMethod("GetDataProvider").MakeGenericMethod(nameProviderType);
            var nameProvider = getDataProvider.Invoke(provider, null);
            if (nameProvider == null)
                throw new InvalidOperationException("Unity Sprite Editor has no name/file-id provider.");
            nameProviderType.GetMethod("SetNameFileIdPairs").Invoke(nameProvider, new object[] { pairs });
            providerType.GetMethod("Apply").Invoke(provider, null);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        private static Type FindSpriteEditorType(string fullName)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null)
                throw new InvalidOperationException("Sprite Editor package is required for sprite slicing in Unity 6.");
            return type;
        }
#endif

        private static void ValidateBorder(SpriteBorderInput border, int width, int height)
        {
            if (border.left < 0 || border.right < 0 || border.top < 0 || border.bottom < 0
                || border.left + border.right > width || border.top + border.bottom > height)
                throw new ArgumentException("Sprite border is outside the texture.", "json");
        }

        private static SpriteLayoutData BuildSpriteLayout(string assetPath, Texture2D texture, TextureImporter importer)
        {
            var border = importer.spriteBorder;
            var sheet = importer.spriteImportMode == SpriteImportMode.Multiple
                ? AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Sprite>().OrderBy(sprite => sprite.name, StringComparer.Ordinal).ToArray()
                : Array.Empty<Sprite>();
            return new SpriteLayoutData
            {
                path = assetPath,
                width = texture.width,
                height = texture.height,
                mode = importer.spriteImportMode == SpriteImportMode.Multiple ? "multiple" : "single",
                border = new SpriteBorderInput
                {
                    left = border.x,
                    bottom = border.y,
                    right = border.z,
                    top = border.w
                },
                slices = sheet.Select(item => new SpriteSliceInput
                {
                    name = item.name,
                    x = item.rect.x,
                    y = item.rect.y,
                    width = item.rect.width,
                    height = item.rect.height
                }).ToArray()
            };
        }

        [Serializable]
        private sealed class SpriteMutationPayload
        {
            public SpriteSliceInput[] slices = Array.Empty<SpriteSliceInput>();
            public SpriteBorderInput border;
        }

        [Serializable]
        private sealed class SpriteLayoutData
        {
            public string path;
            public int width;
            public int height;
            public string mode;
            public SpriteSliceInput[] slices = Array.Empty<SpriteSliceInput>();
            public SpriteBorderInput border;
        }

        [Serializable]
        private sealed class SpriteSliceInput
        {
            public string name;
            public float x;
            public float y;
            public float width;
            public float height;
        }

        [Serializable]
        private sealed class SpriteBorderInput
        {
            public float left;
            public float right;
            public float top;
            public float bottom;
        }

        public static AssetData Move(string assetPath, string destinationPath)
        {
            assetPath = ValidateExistingAssetPath(assetPath);
            destinationPath = NormalizeAssetPath(destinationPath);
            EnsureFolder(Path.GetDirectoryName(destinationPath).Replace('\\', '/'));
            var error = AssetDatabase.MoveAsset(assetPath, destinationPath);
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException(error);
            AssetDatabase.SaveAssets();
            return GetInfo(destinationPath);
        }

        // A Shader Graph window left open on a deleted graph stops Unity with a modal "Graph removed from project";
        // the developer closes the graph first. The plugin keeps graphs saved, so closing asks nothing.
        private static void CloseGraphWindows(string assetPath)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(item => item.GetType().Name == "MaterialGraphEditWindow"))
            {
                var guid = window.GetType().GetProperty("selectedGuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(window, null) as string;
                var path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
                if (path == assetPath || path != null && path.StartsWith(assetPath + "/", StringComparison.Ordinal))
                    window.Close();
            }
        }

        // Edit > Duplicate: without a destination the copy is named as the Project window names it ("Name 1").
        public static AssetData Duplicate(string assetPath, string destinationPath)
        {
            assetPath = ValidateExistingAssetPath(assetPath);
            destinationPath = string.IsNullOrWhiteSpace(destinationPath)
                ? AssetDatabase.GenerateUniqueAssetPath(assetPath)
                : NormalizeAssetPath(destinationPath);
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(destinationPath, AssetPathToGUIDOptions.OnlyExistingAssets)))
                throw new IOException("Asset already exists: " + destinationPath);
            EnsureFolder(Path.GetDirectoryName(destinationPath).Replace('\\', '/'));
            if (!AssetDatabase.CopyAsset(assetPath, destinationPath))
                throw new InvalidOperationException("Unity could not duplicate " + assetPath + " to " + destinationPath);
            EditorPresentationService.ShowAssetPath(destinationPath);
            return GetInfo(destinationPath);
        }

        public static string Delete(string assetPath)
        {
            assetPath = ValidateExistingAssetPath(assetPath);
            CloseGraphWindows(assetPath);
            if (!AssetDatabase.DeleteAsset(assetPath))
                throw new InvalidOperationException("Unity could not delete asset: " + assetPath);
            AssetDatabase.SaveAssets();
            return "Asset deleted: " + assetPath;
        }

        public static AssetData Modify(string assetPath, PropertyValue[] values, bool confirm = false)
        {
            if (values == null || values.Length == 0)
                throw new InvalidOperationException("Asset modification requires at least one property value.");
            var changes = new List<string>();
            if (AssetViews.IsVirtualPath(assetPath))
            {
                AssetViews.Find(assetPath, null).Modify(assetPath, null, values, confirm, changes);
                return new AssetData { name = assetPath.Substring(assetPath.LastIndexOf('/') + 1), assetPath = assetPath, changes = changes.ToArray() };
            }
            string subAsset;
            assetPath = ValidateExistingAssetPath(SplitSubAsset(assetPath, out subAsset));
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (subAsset != null)
                asset = FindSubAsset(assetPath, subAsset);
            var view = AssetViews.Find(assetPath, asset);
            if (view != null)
            {
                if (!view.OwnsWindow)
                    EditorPresentationService.ShowAssetPath(assetPath);
                values = view.Modify(assetPath, asset, values, confirm, changes);
                if (values.Length == 0)
                    return new AssetData { name = asset.name, assetPath = assetPath, changes = changes.ToArray() };
            }
            var importer = subAsset == null ? AssetImporter.GetAtPath(assetPath) : null;
            var assetObject = asset == null ? null : new SerializedObject(asset);
            var importerObject = importer == null ? null : new SerializedObject(importer);
            // A material's Inspector shows its shader properties, so their names and labels set them as material-modify does.
            var material = asset as Material;
            if (material != null)
            {
                var shaderValues = values.Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.path) && !entry.path.Contains(":") &&
                    assetObject.FindProperty(entry.path) == null && ShaderPropertyName(material.shader, entry.path) != null).ToArray();
                if (shaderValues.Length > 0)
                {
                    foreach (var name in SetMaterialValues(material, shaderValues))
                        changes.Add(name + " = " + ShaderProperty(material.shader, material, material.shader.FindPropertyIndex(name)).value);
                    values = values.Except(shaderValues).ToArray();
                    if (values.Length == 0)
                    {
                        EditorPresentationService.ShowAsset(material);
                        return new AssetData { name = asset.name, assetPath = assetPath, changes = changes.ToArray() };
                    }
                    assetObject = new SerializedObject(asset);
                }
            }
            var importerChanged = false;
            var assetChanged = false;
            var expected = new List<ExpectedAssetValue>();

            foreach (var entry in values)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.path))
                    throw new InvalidOperationException("Every asset value requires a serialized property path.");
                string source;
                string propertyPath;
                ResolvePropertyPath(entry.path, assetObject, importerObject, out source, out propertyPath);
                if (source == "asset")
                {
                    var assetProperty = assetObject == null ? null : assetObject.FindProperty(propertyPath);
                    if (assetProperty == null)
                        throw new InvalidOperationException("Serialized asset property was not found: " + propertyPath);
                    ComponentService.SetProperty(assetProperty, entry.value);
                    expected.Add(new ExpectedAssetValue
                    {
                        source = "asset",
                        path = assetProperty.propertyPath,
                        value = ComponentService.ComparableValue(assetProperty)
                    });
                    assetChanged = true;
                    continue;
                }
                var importerProperty = importerObject == null ? null : importerObject.FindProperty(propertyPath);
                if (importerProperty == null)
                    throw new InvalidOperationException("Serialized importer property was not found: " + propertyPath);
                ComponentService.SetProperty(importerProperty, entry.value);
                expected.Add(new ExpectedAssetValue
                {
                    source = "importer",
                    path = importerProperty.propertyPath,
                    value = ComponentService.ComparableValue(importerProperty)
                });
                importerChanged = true;
            }

            if (assetChanged)
            {
                assetObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
            }
            if (importerChanged)
            {
                importerObject.ApplyModifiedPropertiesWithoutUndo();
                importer.SaveAndReimport();
            }
            asset = subAsset == null ? AssetDatabase.LoadMainAssetAtPath(assetPath) : FindSubAsset(assetPath, subAsset);
            importer = subAsset == null ? AssetImporter.GetAtPath(assetPath) : null;
            assetObject = asset == null ? null : new SerializedObject(asset);
            importerObject = importer == null ? null : new SerializedObject(importer);
            foreach (var item in expected)
            {
                var owner = item.source == "asset" ? assetObject : importerObject;
                var property = owner == null ? null : owner.FindProperty(item.path);
                if (property == null || !string.Equals(ComponentService.ComparableValue(property), item.value, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unity did not retain the serialized value: " + item.source + ":" + item.path + ".");
                changes.Add(item.path + " = " + AssetViews.Printable(AssetViews.Display(property)));
            }
            EditorPresentationService.RevealProperties(asset, expected.Where(item => item.source == "asset").Select(item => item.path));
            EditorPresentationService.RevealProperties(importer, expected.Where(item => item.source != "asset").Select(item => item.path));
            var info = GetInfo(subAsset == null ? assetPath : assetPath + "#" + subAsset);
            info.changes = changes.ToArray();
            return info;
        }

        public static CandidateData[] GetObjectPickerCandidates(string assetPath, string propertyPath, int limit)
        {
            assetPath = ValidateExistingAssetPath(assetPath);
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                throw new InvalidOperationException("Asset could not be loaded: " + assetPath);
            EditorPresentationService.ShowAsset(asset);
            var importer = AssetImporter.GetAtPath(assetPath);
            string source;
            string rawPropertyPath;
            ResolvePropertyPath(
                propertyPath,
                asset == null ? null : new SerializedObject(asset),
                importer == null ? null : new SerializedObject(importer),
                out source,
                out rawPropertyPath);
            var property = source == "asset"
                ? asset == null ? null : new SerializedObject(asset).FindProperty(rawPropertyPath)
                : importer == null ? null : new SerializedObject(importer).FindProperty(rawPropertyPath);
            if (property == null)
                throw new InvalidOperationException("Serialized " + source + " property was not found: " + rawPropertyPath);
            if (property.propertyType != SerializedPropertyType.ObjectReference)
                throw new InvalidOperationException("Asset Object Picker requires an object-reference property: " + propertyPath);
            var targetType = ComponentService.ResolveObjectReferenceType(property.type);

            return AssetDatabase.FindAssets("t:" + targetType.Name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .OrderBy(path => path.StartsWith("Assets/", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(path => path, StringComparer.Ordinal)
                .SelectMany(path => AssetDatabase.LoadAllAssetsAtPath(path)
                    .Where(asset => asset != null && targetType.IsAssignableFrom(asset.GetType()))
                    .GroupBy(asset => asset.GetType())
                    .SelectMany(group => group.Select((asset, index) => new
                    {
                        path = path + "#" + Uri.EscapeDataString(asset.GetType().FullName) + "[" + index + "]",
                        asset
                    })))
                .Take(limit)
                .Select(item => new CandidateData
                {
                    label = item.asset.name,
                    path = item.path,
                    type = item.asset.GetType().FullName,
                    source = "asset"
                })
                .ToArray();
        }

        private static void AddProperties(UnityEngine.Object target, string source, ICollection<SerializedPropertyData> result)
        {
            var serialized = new SerializedObject(target);
            var iterator = serialized.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                // Fields kept only to migrate old data (HDRP's m_Obsolete…) are not shown by the Inspector.
                if (SerializedField(target.GetType(), iterator.name)?.IsDefined(typeof(ObsoleteAttribute), false) == true)
                    continue;
                result.Add(new SerializedPropertyData
                {
                    path = source + ":" + iterator.propertyPath,
                    type = iterator.propertyType.ToString(),
                    value = PropertyValue(iterator),
                    writable = iterator.propertyPath != "m_Script"
                });
            }
        }

        private static void AddSelectedProperties(
            UnityEngine.Object asset,
            AssetImporter importer,
            string qualifiedPath,
            ICollection<SerializedPropertyData> result)
        {
            var serializedAsset = new SerializedObject(asset);
            var serializedImporter = importer == null ? null : new SerializedObject(importer);
            string source;
            string propertyPath;
            ResolvePropertyPath(qualifiedPath, serializedAsset, serializedImporter, out source, out propertyPath);
            var serialized = source == "asset" ? serializedAsset : serializedImporter;
            var property = serialized.FindProperty(propertyPath);
            if (property == null)
                throw new ArgumentException("Serialized property was not found: " + qualifiedPath, "qualifiedPath");
            AddProperty(property, source, result);
            if (property.isArray)
                return;
            // One level, as a foldout opens: a nested struct reads as {…} and opens with its own --property.
            foreach (var child in AssetViews.ChildProperties(property))
                AddProperty(child, source, result);
        }

        private static FieldInfo SerializedField(Type type, string name)
        {
            for (var current = type; current != null && current != typeof(UnityEngine.Object); current = current.BaseType)
            {
                var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            return null;
        }

        private static void AddProperty(SerializedProperty property, string source, ICollection<SerializedPropertyData> result)
        {
            result.Add(new SerializedPropertyData
            {
                path = source + ":" + property.propertyPath,
                type = property.propertyType.ToString(),
                value = PropertyValue(property),
                writable = property.propertyPath != "m_Script"
            });
        }

        private static void AddScriptFields(Type type, ICollection<SerializedPropertyData> result)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => field.IsPublic || field.GetCustomAttributes(typeof(SerializeField), true).Length > 0)
                .OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                result.Add(new SerializedPropertyData
                {
                    path = "script:" + field.Name,
                    type = field.FieldType.FullName,
                    value = string.Empty,
                    writable = false
                });
            }
        }

        private static string PropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return property.doubleValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Enum: return property.enumNames.Length > property.enumValueIndex && property.enumValueIndex >= 0 ? property.enumNames[property.enumValueIndex] : property.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference: return property.objectReferenceValue == null ? string.Empty : AssetDatabase.GetAssetPath(property.objectReferenceValue);
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString("G9");
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString("G9");
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString("G9");
                case SerializedPropertyType.Color: return property.colorValue.ToString();
                default: return property.isArray ? "Array[" + property.arraySize + "]" : property.hasVisibleChildren ? "{…}" : string.Empty;
            }
        }

        private static string ValidateNewAssetPath(string path, string extension)
        {
            path = NormalizeAssetPath(path);
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Creation Template requires extension " + extension + ".");
            if (File.Exists(ToFullPath(path)) || AssetDatabase.LoadMainAssetAtPath(path) != null)
                throw new IOException("Asset already exists: " + path);
            return path;
        }

        private static void SplitPropertyPath(string qualifiedPath, out string source, out string propertyPath)
        {
            var separator = qualifiedPath == null ? -1 : qualifiedPath.IndexOf(':');
            if (separator <= 0 || separator == qualifiedPath.Length - 1)
                throw new ArgumentException("Property must use asset:<path> or importer:<path>.", "qualifiedPath");
            source = qualifiedPath.Substring(0, separator);
            propertyPath = qualifiedPath.Substring(separator + 1);
            if (source != "asset" && source != "importer")
                throw new ArgumentException("Writable property source must be 'asset' or 'importer'.", "qualifiedPath");
        }

        private static void ResolvePropertyPath(
            string path,
            SerializedObject asset,
            SerializedObject importer,
            out string source,
            out string propertyPath)
        {
            if (!string.IsNullOrWhiteSpace(path) && path.IndexOf(':') < 0)
            {
                var inAsset = asset != null && asset.FindProperty(path) != null;
                var inImporter = importer != null && importer.FindProperty(path) != null;
                if (inAsset == inImporter)
                    throw new ArgumentException(inAsset
                        ? "Property exists on both asset and importer; use asset:<path> or importer:<path>."
                        : "Serialized asset or importer property was not found: " + path,
                        "path");
                source = inAsset ? "asset" : "importer";
                propertyPath = path;
                return;
            }
            SplitPropertyPath(path, out source, out propertyPath);
        }

        private static string ValidateExistingAssetPath(string path)
        {
            path = NormalizeAssetPath(path);
            if (!File.Exists(ToFullPath(path)) && !Directory.Exists(ToFullPath(path)))
                throw new FileNotFoundException("Asset was not found.", path);
            return path;
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Asset path is required.", "path");
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains("../") || path.EndsWith("/", StringComparison.Ordinal))
                throw new ArgumentException("Asset path must point to a file below Assets.", "path");
            ToFullPath(path);
            return path;
        }

        private static string ToFullPath(string assetPath)
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var full = Path.GetFullPath(Path.Combine(root, assetPath));
            if (!full.StartsWith(Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Asset path escaped the project's Assets folder.");
            return full;
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets" || AssetDatabase.IsValidFolder(folder))
                return;
            EnsureFolder(Path.GetDirectoryName(folder).Replace('\\', '/'));
            AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
        }

        private static void WriteText(string assetPath, string contents)
        {
            File.WriteAllText(ToFullPath(assetPath), contents, new UTF8Encoding(false));
        }

        private static string ClassName(string assetPath)
        {
            var name = Regex.Replace(Path.GetFileNameWithoutExtension(assetPath), "[^A-Za-z0-9_]", string.Empty);
            if (string.IsNullOrEmpty(name) || char.IsDigit(name[0]))
                throw new InvalidOperationException("Asset file name cannot form a valid C# class name: " + assetPath);
            return name;
        }
    }
}
