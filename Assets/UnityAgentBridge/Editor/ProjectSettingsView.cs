using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAgentBridge.Editor
{
    // "Project Settings/<Section>[/<Tab>]" behaves like Edit > Project Settings: the same sections, labels and buttons.
    internal sealed class ProjectSettingsView : AssetView
    {
        private const string Prefix = "Project Settings";
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] Sections =
        {
            "Tags and Layers", "Physics", "Physics 2D", "Player", "Quality", "Graphics", "Time", "Audio", "Editor", "Script Execution Order", "TextMesh Pro"
        };

        private const string TmpSettingsAsset = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

        private static readonly Dictionary<string, string> ManagerAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Tags and Layers", "ProjectSettings/TagManager.asset" },
            { "Physics", "ProjectSettings/DynamicsManager.asset" },
            { "Physics 2D", "ProjectSettings/Physics2DSettings.asset" },
            { "Player", "ProjectSettings/ProjectSettings.asset" },
            { "Quality", "ProjectSettings/QualitySettings.asset" },
            { "Graphics", "ProjectSettings/GraphicsSettings.asset" },
            { "Time", "ProjectSettings/TimeManager.asset" },
            { "Audio", "ProjectSettings/AudioManager.asset" },
            { "Editor", "ProjectSettings/EditorSettings.asset" },
            { "TextMesh Pro", TmpSettingsAsset }
        };

        internal static bool IsProjectSettingsPath(string path)
        {
            return path != null && (path.Equals(Prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase));
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return IsProjectSettingsPath(path);
        }

        private static void Split(string path, out string section, out string tab)
        {
            var rest = path.Length > Prefix.Length ? path.Substring(Prefix.Length + 1).Trim('/') : string.Empty;
            section = string.Empty;
            tab = string.Empty;
            if (rest.Length == 0)
                return;
            section = Sections.Where(item => rest.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                    rest.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Length).FirstOrDefault();
            if (section == null)
                throw new ArgumentException("Unknown Project Settings section: " + rest + ". Sections: " + string.Join(", ", Sections));
            tab = rest.Length > section.Length ? rest.Substring(section.Length + 1) : string.Empty;
        }

        private static SerializedObject Manager(string section)
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath(ManagerAssets[section]).FirstOrDefault();
            if (asset == null)
                throw new InvalidOperationException(section == "TextMesh Pro"
                    ? "TMP Essential Resources are not imported; run the Import TMP Essentials action."
                    : "Project Settings asset is missing: " + ManagerAssets[section]);
            return new SerializedObject(asset);
        }

        private static void Open(string section, string tab)
        {
            if (section.Length == 0)
            {
                SettingsService.OpenProjectSettings("Project");
                return;
            }
            if (section == "Graphics" && tab.Length > 0)
            {
                var pipeline = PipelineAssetType(tab);
                var utility = FindType("UnityEditor.Inspector.GraphicsSettingsInspectors.GraphicsSettingsInspectorUtility");
                var open = utility == null ? null : utility.GetMethod("OpenAndScrollTo", AnyStatic, null, new[] { typeof(Type) }, null);
                var settings = GlobalSettings(pipeline);
                if (open != null && settings != null)
                {
                    var first = GlobalEntries(settings).Select(entry => entry.managedReferenceValue).FirstOrDefault(value => value != null);
                    if (first != null)
                    {
                        open.Invoke(null, new object[] { first.GetType() });
                        return;
                    }
                }
            }
            // TMP shows its import buttons on "TextMesh Pro" and the settings asset on its Settings child page.
            SettingsService.OpenProjectSettings("Project/" + section + (section == "TextMesh Pro" && TmpImported() ? "/Settings" : string.Empty));
        }

        private static bool TmpImported()
        {
            return AssetDatabase.LoadMainAssetAtPath(TmpSettingsAsset) != null;
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            string section, tab;
            Split(path, out section, out tab);
            Open(section, tab);
            var result = Section(section, tab, property);
            return string.IsNullOrEmpty(property) || section == "Graphics" && tab.Length > 0 ? result : Pick(section, result, property);
        }

        // --property reads one setting of the section, a struct by its fields.
        private static JsonText Pick(string section, JsonText fields, string property)
        {
            foreach (var pair in fields.Items.Concat(fields.Items.Select(item => item.Value).OfType<JsonText>().SelectMany(group => group.Items)))
            {
                if (!AssetViews.KeyIs(property, pair.Key))
                    continue;
                var serialized = ManagerAssets.ContainsKey(section) && pair.Value as string == "{…}" ? AssetViews.FindByLabel(Manager(section), pair.Key) : null;
                return new JsonText().Add(pair.Key, serialized == null ? pair.Value : AssetViews.Children(serialized));
            }
            throw new ArgumentException(section + " setting was not found: " + property + "; asset-info without --property lists them.");
        }

        private static JsonText Section(string section, string tab, string property)
        {
            switch (section)
            {
                case "": return new JsonText().Add("sections", Sections);
                case "Tags and Layers": return TagsAndLayers();
                case "Physics": return Physics(false);
                case "Physics 2D": return Physics(true);
                case "Player": return Player(tab);
                case "Quality": return Quality(tab);
                case "Graphics": return Graphics(tab, property);
                case "Script Execution Order": return ExecutionOrder();
                case "Time": return TimeSettings();
                case "TextMesh Pro":
                    if (!TmpImported())
                        return new JsonText().Add("TMP Essential Resources", "not imported");
                    return AssetViews.Fields(Manager(section));
                default: return AssetViews.Fields(Manager(section));
            }
        }

        // Unity 6 stores Fixed Timestep as a rational number; the Inspector shows seconds.
        private static JsonText TimeSettings()
        {
            var result = new JsonText().Add("Fixed Timestep", Math.Round(Time.fixedDeltaTime, 6));
            foreach (var pair in AssetViews.Fields(Manager("Time"), property => property.name != "Fixed Timestep").Items)
                result.Add(pair.Key, pair.Value);
            return result;
        }

        private static bool SetFixedTimestep(string key, string rawValue)
        {
            if (!AssetViews.KeyIs(key, "Fixed Timestep"))
                return false;
            Time.fixedDeltaTime = float.Parse(AssetViews.Text(rawValue), NumberStyles.Float, CultureInfo.InvariantCulture);
            return true;
        }

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            string section, tab;
            Split(path, out section, out tab);
            switch (section)
            {
                case "Tags and Layers":
                    return new[] { "Add Tag", "Remove Tag", "Add Sorting Layer", "Remove Sorting Layer", "Add Rendering Layer", "Remove Rendering Layer" };
                case "Quality":
                    return tab.Length == 0 ? new[] { "Add Quality Level", "Remove Quality Level" } : Array.Empty<string>();
                case "TextMesh Pro":
                    return TmpImported() ? new[] { "Import TMP Examples & Extras" } : new[] { "Import TMP Essentials" };
                default:
                    return Array.Empty<string>();
            }
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            RequireEditMode();
            string section, tab;
            Split(path, out section, out tab);
            Open(section, tab);
            action = AssetViews.Match(Actions(path, asset), action);
            if (action.StartsWith("Import TMP", StringComparison.Ordinal))
                return ImportTmp(action == "Import TMP Essentials");
            var name = AssetViews.Value(values, "name", action != "Remove Rendering Layer");
            if (section == "Tags and Layers")
                RevealTagList(action.Contains("Sorting") ? "Sorting Layers" : action.Contains("Rendering") ? "Rendering Layers" : "Tags");
            switch (action)
            {
                case "Add Tag":
                    if (InternalEditorUtility.tags.Contains(name))
                        throw new InvalidOperationException("Tag already exists: " + name);
                    InternalEditorUtility.AddTag(name);
                    return Saved("Tag added: " + name, "Tags and Layers");
                case "Remove Tag":
                    if (!InternalEditorUtility.tags.Contains(name))
                        throw new InvalidOperationException("Tag was not found: " + name);
                    InternalEditorUtility.RemoveTag(name);
                    return Saved("Tag removed: " + name, "Tags and Layers");
                case "Add Sorting Layer":
                    if (SortingLayer.layers.Any(layer => layer.name == name))
                        throw new InvalidOperationException("Sorting layer already exists: " + name);
                    Internal("AddSortingLayer");
                    Internal("SetSortingLayerName", SortingLayer.layers.Length - 1, name);
                    return Saved("Sorting layer added: " + name, "Tags and Layers");
                case "Remove Sorting Layer":
                    return RemoveSortingLayer(name);
                case "Add Rendering Layer":
                    if (!UnityEditor.Rendering.RenderPipelineEditorUtility.TryAddRenderingLayerName(name))
                        throw new InvalidOperationException("Unity did not add the rendering layer (all layers are used?): " + name);
                    return Saved("Rendering layer added: " + name, "Tags and Layers");
                case "Remove Rendering Layer":
                    var last = RenderingLayerMask.GetDefinedRenderingLayerNames().LastOrDefault();
                    if (!UnityEditor.Rendering.RenderPipelineEditorUtility.TryRemoveLastRenderingLayerName())
                        throw new InvalidOperationException("Unity removes only the last rendering layer and keeps Default.");
                    return Saved("Rendering layer removed: " + last, "Tags and Layers");
                case "Add Quality Level":
                    return AddQualityLevel(name);
                case "Remove Quality Level":
                    return RemoveQualityLevel(name);
            }
            throw new InvalidOperationException("Unknown asset action: " + action);
        }

        // Unity does not write Project Settings in Play Mode and restores them when the game stops.
        private static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Project Settings are not saved in Play Mode; Unity restores them when the game stops. Run play stop first.");
        }

        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            RequireEditMode();
            string section, tab;
            Split(path, out section, out tab);
            if (section.Length == 0)
                throw new ArgumentException("Choose a section: Project Settings/<" + string.Join("|", Sections) + ">.");
            Open(section, tab);
            switch (section)
            {
                case "Tags and Layers":
                    RevealTagList("Layers");
                    ModifyManager(section, values, changes, SetLayer);
                    break;
                case "Physics":
                case "Physics 2D":
                    ModifyManager(section, values, changes, (key, value) => SetCollision(section == "Physics 2D", key, value));
                    break;
                case "Player":
                    ModifyPlayer(tab, values, confirm, changes);
                    break;
                case "Quality":
                    ModifyQuality(tab, values, changes);
                    break;
                case "Graphics":
                    ModifyGraphics(tab, values, changes);
                    break;
                case "Script Execution Order":
                    foreach (var entry in values)
                        changes.Add(SetExecutionOrder(entry.path, AssetViews.Int(entry.value)));
                    break;
                case "Time":
                    ModifyManager(section, values, changes, SetFixedTimestep);
                    break;
                default:
                    ModifyManager(section, values, changes, null);
                    break;
            }
            AssetDatabase.SaveAssets();
            return Array.Empty<PropertyValue>();
        }

        // ---------- Tags and Layers ----------

        private static JsonText TagsAndLayers()
        {
            var layers = new JsonText();
            for (var index = 0; index < 32; index++)
            {
                var name = LayerMask.LayerToName(index);
                if (!string.IsNullOrEmpty(name))
                    layers.Add(index.ToString(CultureInfo.InvariantCulture), name);
            }
            return new JsonText()
                .Add("Tags", InternalEditorUtility.tags.Where(tag => !IsBuiltInTag(tag)).ToArray())
                .Add("Layers", layers)
                .Add("Sorting Layers", SortingLayer.layers.Select(layer => layer.name).ToArray())
                .Add("Rendering Layers", RenderingLayerMask.GetDefinedRenderingLayerNames());
        }

        // The edited list is unfolded as the developer opens it: live on an open page, through Unity's own initial
        // expansion (as Inspector's "Add Tag..." does) when the page is still being built.
        private static void RevealTagList(string label)
        {
            var found = false;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(item => item.GetType().Name == "ProjectSettingsWindow"))
                UnityEngine.UIElements.UQueryExtensions.Query<UnityEngine.UIElements.Foldout>(window.rootVisualElement).ForEach(foldout =>
                {
                    if (foldout.text != label)
                        return;
                    foldout.value = true;
                    found = true;
                });
            if (found)
                return;
            var inspector = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.TagManagerInspector");
            var state = inspector == null ? null : inspector.GetNestedType("InitialExpansionState", BindingFlags.Public | BindingFlags.NonPublic);
            var show = inspector == null ? null : inspector.GetMethod("ShowWithInitialExpansion", AnyStatic);
            if (state != null && show != null)
                show.Invoke(null, new[] { Enum.Parse(state, label.Replace(" ", string.Empty)) });
        }

        private static bool IsBuiltInTag(string tag)
        {
            return new[] { "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController" }.Contains(tag);
        }

        // "Layer8=Enemy" or "User Layer 8" names layer 8; built-in layers 0,1,2,4,5 stay read-only like in the Inspector.
        private static bool SetLayer(string key, string rawValue)
        {
            var digits = new string(key.Where(char.IsDigit).ToArray());
            if (!AssetViews.Normalize(key).StartsWith("layer", StringComparison.Ordinal) &&
                !AssetViews.Normalize(key).StartsWith("userlayer", StringComparison.Ordinal) || digits.Length == 0)
                return false;
            var index = int.Parse(digits, CultureInfo.InvariantCulture);
            if (index < 3 || index == 4 || index == 5 || index > 31)
                throw new InvalidOperationException("Layer " + index + " is built in; user layers are 3, 6-31.");
            var manager = Manager("Tags and Layers");
            manager.FindProperty("layers").GetArrayElementAtIndex(index).stringValue = AssetViews.Text(rawValue);
            manager.ApplyModifiedProperties();
            return true;
        }

        private static string RemoveSortingLayer(string name)
        {
            var manager = Manager("Tags and Layers");
            var layers = manager.FindProperty("m_SortingLayers");
            for (var index = 0; index < layers.arraySize; index++)
            {
                if (layers.GetArrayElementAtIndex(index).FindPropertyRelative("name").stringValue != name)
                    continue;
                if (layers.GetArrayElementAtIndex(index).FindPropertyRelative("uniqueID").longValue == 0)
                    throw new InvalidOperationException("The Default sorting layer cannot be removed.");
                layers.DeleteArrayElementAtIndex(index);
                manager.ApplyModifiedProperties();
                Internal("UpdateSortingLayersOrder");
                return Saved("Sorting layer removed: " + name, "Tags and Layers");
            }
            throw new InvalidOperationException("Sorting layer was not found: " + name);
        }

        private static void Internal(string method, params object[] arguments)
        {
            var info = typeof(InternalEditorUtility).GetMethods(AnyStatic)
                .FirstOrDefault(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length);
            if (info == null)
                throw new MissingMethodException(typeof(InternalEditorUtility).FullName, method);
            info.Invoke(null, arguments);
        }

        // The buttons of Project Settings > TextMesh Pro: unitypackages that ship inside the ugui package.
        private static string ImportTmp(bool essentials)
        {
            if (!essentials && !TmpImported())
                throw new InvalidOperationException("Import TMP Essentials first, as the Project Settings page requires.");
            TmpEssentialsService.Import(essentials);
            return essentials ? "TMP Essential Resources imported into Assets/TextMesh Pro" : "TMP Examples & Extras imported into Assets/TextMesh Pro";
        }

        // ---------- Physics ----------

        private static JsonText Physics(bool twoD)
        {
            var manager = Manager(twoD ? "Physics 2D" : "Physics");
            var result = AssetViews.Fields(manager, property => property.name != "m_LayerCollisionMatrix" && property.name != "m_CurrentBackendId");
            result.Add("Layer Collision Matrix", IgnoredPairs(twoD));
            return result;
        }

        // Checked cells of the matrix collide; only the unchecked pairs are listed.
        private static string IgnoredPairs(bool twoD)
        {
            var named = Enumerable.Range(0, 32).Where(index => !string.IsNullOrEmpty(LayerMask.LayerToName(index))).ToArray();
            var pairs = new List<string>();
            foreach (var a in named)
                foreach (var b in named.Where(b => b >= a))
                    if (twoD ? Physics2D.GetIgnoreLayerCollision(a, b) : UnityEngine.Physics.GetIgnoreLayerCollision(a, b))
                        pairs.Add(LayerMask.LayerToName(a) + "/" + LayerMask.LayerToName(b));
            return pairs.Count == 0 ? "all layers collide" : "ignored: " + string.Join(", ", pairs);
        }

        // "LayerCollisionMatrix.Player/Enemy=false" unchecks the cell; layer numbers work for names with spaces.
        private static bool SetCollision(bool twoD, string key, string rawValue)
        {
            var separator = key.IndexOf('.');
            if (separator < 0 || AssetViews.Normalize(key.Substring(0, separator)) != "layercollisionmatrix")
                return false;
            var layers = key.Substring(separator + 1).Split('/');
            if (layers.Length != 2)
                throw new ArgumentException("Use LayerCollisionMatrix.<layer>/<layer>=true|false.");
            var a = LayerIndex(layers[0]);
            var b = LayerIndex(layers[1]);
            var collide = AssetViews.Bool(rawValue);
            if (twoD)
                Physics2D.IgnoreLayerCollision(a, b, !collide);
            else
                UnityEngine.Physics.IgnoreLayerCollision(a, b, !collide);
            return true;
        }

        private static int LayerIndex(string value)
        {
            int index;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < 32 &&
                !string.IsNullOrEmpty(LayerMask.LayerToName(index)))
                return index;
            for (index = 0; index < 32; index++)
            {
                var name = LayerMask.LayerToName(index);
                if (!string.IsNullOrEmpty(name) && AssetViews.Normalize(name) == AssetViews.Normalize(value))
                    return index;
            }
            throw new ArgumentException("Layer was not found: " + value);
        }

        // ---------- Player ----------

        private sealed class PlayerField
        {
            internal string group;
            internal string label;
            internal Func<NamedBuildTarget, object> get;
            internal Action<NamedBuildTarget, string> set;
            internal string restart;
        }

        private static readonly PlayerField[] PlayerFields =
        {
            Field(null, "Company Name", target => PlayerSettings.companyName, (target, value) => PlayerSettings.companyName = AssetViews.Text(value)),
            Field(null, "Product Name", target => PlayerSettings.productName, (target, value) => PlayerSettings.productName = AssetViews.Text(value)),
            Field(null, "Version", target => PlayerSettings.bundleVersion, (target, value) => PlayerSettings.bundleVersion = AssetViews.Text(value)),
            Field(null, "Default Icon", target => AssetViews.ObjectLabel(PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any).FirstOrDefault()),
                (target, value) => PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { LoadAsset<Texture2D>(value) }, IconKind.Any)),
            Field("Resolution and Presentation", "Fullscreen Mode", target => ObjectNames.NicifyVariableName(PlayerSettings.fullScreenMode.ToString()),
                (target, value) => PlayerSettings.fullScreenMode = EnumValue<FullScreenMode>(value)),
            Field("Resolution and Presentation", "Default Screen Width", target => PlayerSettings.defaultScreenWidth, (target, value) => PlayerSettings.defaultScreenWidth = AssetViews.Int(value)),
            Field("Resolution and Presentation", "Default Screen Height", target => PlayerSettings.defaultScreenHeight, (target, value) => PlayerSettings.defaultScreenHeight = AssetViews.Int(value)),
            Field("Resolution and Presentation", "Run In Background", target => PlayerSettings.runInBackground, (target, value) => PlayerSettings.runInBackground = AssetViews.Bool(value)),
            Field("Resolution and Presentation", "Resizable Window", target => PlayerSettings.resizableWindow, (target, value) => PlayerSettings.resizableWindow = AssetViews.Bool(value)),
            Field("Splash Image", "Show Splash Screen", target => PlayerSettings.SplashScreen.show, (target, value) => PlayerSettings.SplashScreen.show = AssetViews.Bool(value)),
            Field("Splash Image", "Show Unity Logo", target => PlayerSettings.SplashScreen.showUnityLogo, (target, value) => PlayerSettings.SplashScreen.showUnityLogo = AssetViews.Bool(value)),
            Field("Other Settings", "Color Space", target => PlayerSettings.colorSpace.ToString(), (target, value) => PlayerSettings.colorSpace = EnumValue<ColorSpace>(value),
                "reimports every texture and rebuilds shaders"),
            Field("Other Settings", "Active Input Handling", target => InputHandling(), (target, value) => SetInputHandling(value),
                "applies after the editor restarts"),
            Field("Other Settings", "Scripting Backend", target => PlayerSettings.GetScriptingBackend(target).ToString(),
                (target, value) => PlayerSettings.SetScriptingBackend(target, EnumValue<ScriptingImplementation>(value))),
            Field("Other Settings", "Api Compatibility Level", target => PlayerSettings.GetApiCompatibilityLevel(target).ToString(),
                (target, value) => PlayerSettings.SetApiCompatibilityLevel(target, EnumValue<ApiCompatibilityLevel>(value))),
            Field("Other Settings", "Scripting Define Symbols", target => PlayerSettings.GetScriptingDefineSymbols(target),
                (target, value) => PlayerSettings.SetScriptingDefineSymbols(target, AssetViews.Text(value))),
            Field("Other Settings", "Allow Unsafe Code", target => PlayerSettings.allowUnsafeCode, (target, value) => PlayerSettings.allowUnsafeCode = AssetViews.Bool(value)),
            Field("Other Settings", "Application Identifier", target => PlayerSettings.GetApplicationIdentifier(target),
                (target, value) => PlayerSettings.SetApplicationIdentifier(target, AssetViews.Text(value))),
            Field("Other Settings", "Managed Stripping Level", target => PlayerSettings.GetManagedStrippingLevel(target).ToString(),
                (target, value) => PlayerSettings.SetManagedStrippingLevel(target, EnumValue<ManagedStrippingLevel>(value))),
            Field("Other Settings", "Use Incremental GC", target => PlayerSettings.gcIncremental, (target, value) => PlayerSettings.gcIncremental = AssetViews.Bool(value),
                "applies after the editor restarts")
        };

        private static PlayerField Field(string group, string label, Func<NamedBuildTarget, object> get, Action<NamedBuildTarget, string> set, string restart = null)
        {
            return new PlayerField { group = group, label = label, get = get, set = set, restart = restart };
        }

        private static NamedBuildTarget PlatformTab(string tab)
        {
            if (tab.Length == 0)
                return NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
            if (AssetViews.Normalize(tab) == "standalone" || AssetViews.Normalize(tab) == "pc")
                return NamedBuildTarget.Standalone;
            var group = Enum.GetValues(typeof(BuildTargetGroup)).Cast<BuildTargetGroup>()
                .FirstOrDefault(candidate => AssetViews.Normalize(candidate.ToString()) == AssetViews.Normalize(tab));
            if (group == BuildTargetGroup.Unknown)
                throw new ArgumentException("Unknown Player platform tab: " + tab + ". Use Standalone, Android, iOS, WebGL…");
            return NamedBuildTarget.FromBuildTargetGroup(group);
        }

        private static JsonText Player(string tab)
        {
            var target = PlatformTab(tab);
            var result = new JsonText().Add("Platform", target.TargetName);
            var profile = OverridingProfile();
            if (profile != null)
                result.Add("Build Profile", profile);
            foreach (var group in PlayerFields.GroupBy(field => field.group))
            {
                var fields = group.Key == null ? result : new JsonText();
                foreach (var field in group)
                {
                    object value;
                    try { value = field.get(target); }
                    catch (Exception) { continue; }
                    fields.Add(field.label, value);
                }
                if (group.Key != null)
                    result.Add(group.Key, fields);
            }
            return result;
        }

        private static void ModifyPlayer(string tab, PropertyValue[] values, bool confirm, List<string> changes)
        {
            var target = PlatformTab(tab);
            foreach (var entry in values)
            {
                var field = PlayerFields.FirstOrDefault(candidate => AssetViews.KeyIs(entry.path, candidate.label));
                if (field == null)
                {
                    ModifyManager("Player", new[] { entry }, changes, null);
                    continue;
                }
                if (field.restart != null && !confirm)
                    throw new InvalidOperationException("Changing " + field.label + " " + field.restart + "; repeat with --confirm.");
                field.set(target, entry.value);
                changes.Add(field.label + " = " + AssetViews.Printable(field.get(target)) + (field.restart == null ? string.Empty : " (" + field.restart + ")"));
                var profile = OverridingProfile();
                if (profile != null && !changes.Contains("in Build Profile " + profile))
                    changes.Add("in Build Profile " + profile);
            }
        }

        // An active Build Profile with Player Settings Overrides holds the Player settings Unity builds and compiles with;
        // PlayerSettings reads and writes that profile, not ProjectSettings.asset.
        private static string OverridingProfile()
        {
            var profile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
            if (profile == null)
                return null;
            var settings = new SerializedObject(profile).FindProperty("m_PlayerSettingsYaml.m_Settings");
            return settings != null && settings.isArray && settings.arraySize > 0 ? AssetDatabase.GetAssetPath(profile) : null;
        }

        private static string InputHandling()
        {
            var value = Manager("Player").FindProperty("activeInputHandler");
            return value == null ? null : new[] { "Input Manager (Old)", "Input System Package (New)", "Both" }[Mathf.Clamp(value.intValue, 0, 2)];
        }

        private static void SetInputHandling(string rawValue)
        {
            var normalized = AssetViews.Normalize(AssetViews.Text(rawValue));
            var index = normalized.StartsWith("both", StringComparison.Ordinal) ? 2
                : normalized.Contains("system") || normalized.Contains("new") ? 1
                : normalized.Contains("manager") || normalized.Contains("old") ? 0 : -1;
            if (index < 0)
                throw new ArgumentException("Active Input Handling: Input Manager (Old), Input System Package (New) or Both.");
            var manager = Manager("Player");
            manager.FindProperty("activeInputHandler").intValue = index;
            manager.ApplyModifiedProperties();
        }

        private static T EnumValue<T>(string rawValue) where T : struct
        {
            var text = AssetViews.Normalize(AssetViews.Text(rawValue));
            foreach (T value in Enum.GetValues(typeof(T)))
                if (AssetViews.Normalize(value.ToString()) == text)
                    return value;
            throw new ArgumentException(typeof(T).Name + " values: " + string.Join(", ", Enum.GetNames(typeof(T))));
        }

        private static T LoadAsset<T>(string rawValue) where T : UnityEngine.Object
        {
            var path = AssetViews.Text(rawValue);
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new ArgumentException(typeof(T).Name + " was not found: " + path);
            return asset;
        }

        // ---------- Quality ----------

        private static JsonText Quality(string tab)
        {
            var names = QualitySettings.names;
            if (tab.Length == 0)
            {
                // The Inspector's Default row: which level each platform starts with, grouped by level.
                var byLevel = new SortedDictionary<int, List<string>>();
                var map = Manager("Quality").FindProperty("m_PerPlatformDefaultQuality");
                for (var index = 0; map != null && index < map.arraySize; index++)
                {
                    var item = map.GetArrayElementAtIndex(index);
                    var level = item.FindPropertyRelative("second").intValue;
                    if (!byLevel.ContainsKey(level))
                        byLevel[level] = new List<string>();
                    byLevel[level].Add(item.FindPropertyRelative("first").stringValue);
                }
                var defaults = new JsonText();
                foreach (var pair in byLevel)
                    defaults.Add(pair.Key >= 0 && pair.Key < names.Length ? names[pair.Key] : pair.Key.ToString(CultureInfo.InvariantCulture),
                        string.Join(", ", pair.Value));
                return new JsonText()
                    .Add("Levels", names.Select((name, index) => name + (index == QualitySettings.GetQualityLevel() ? " (current)" : string.Empty) +
                        " → " + (AssetViews.ObjectLabel(QualitySettings.GetRenderPipelineAssetAt(index)) ?? "default pipeline")).ToArray())
                    .Add("Default", defaults);
            }
            return AssetViews.Children(QualityLevel(tab));
        }

        private static SerializedProperty QualityLevel(string name)
        {
            var levels = Manager("Quality").FindProperty("m_QualitySettings");
            for (var index = 0; index < levels.arraySize; index++)
                if (AssetViews.Normalize(levels.GetArrayElementAtIndex(index).FindPropertyRelative("name").stringValue) == AssetViews.Normalize(name))
                    return levels.GetArrayElementAtIndex(index);
            throw new ArgumentException("Quality level was not found: " + name + ". Levels: " + string.Join(", ", QualitySettings.names));
        }

        private static int QualityIndex(string name)
        {
            var index = Array.FindIndex(QualitySettings.names, candidate => AssetViews.Normalize(candidate) == AssetViews.Normalize(name));
            if (index < 0)
                throw new ArgumentException("Quality level was not found: " + name + ". Levels: " + string.Join(", ", QualitySettings.names));
            return index;
        }

        private static void ModifyQuality(string tab, PropertyValue[] values, List<string> changes)
        {
            foreach (var entry in values)
            {
                if (tab.Length == 0 && AssetViews.KeyIs(entry.path, "Current", "Level"))
                {
                    QualitySettings.SetQualityLevel(QualityIndex(AssetViews.Text(entry.value)), true);
                    changes.Add("Current = " + QualitySettings.names[QualitySettings.GetQualityLevel()]);
                    continue;
                }
                if (tab.Length == 0 && AssetViews.Normalize(entry.path).StartsWith("default", StringComparison.Ordinal))
                {
                    var platform = entry.path.Substring(entry.path.IndexOf('.') + 1);
                    var level = QualityIndex(AssetViews.Text(entry.value));
                    var manager = Manager("Quality");
                    var map = manager.FindProperty("m_PerPlatformDefaultQuality");
                    var found = false;
                    for (var index = 0; index < map.arraySize; index++)
                    {
                        var item = map.GetArrayElementAtIndex(index);
                        if (AssetViews.Normalize(item.FindPropertyRelative("first").stringValue) != AssetViews.Normalize(platform))
                            continue;
                        item.FindPropertyRelative("second").intValue = level;
                        platform = item.FindPropertyRelative("first").stringValue;
                        found = true;
                    }
                    if (!found)
                        throw new ArgumentException("Platform was not found in Quality defaults: " + platform);
                    manager.ApplyModifiedProperties();
                    changes.Add("Default." + platform + " = " + QualitySettings.names[level]);
                    continue;
                }
                if (tab.Length == 0)
                    throw new ArgumentException("Quality: set Current=<level> or Default.<platform>=<level>; level fields live in Project Settings/Quality/<level>.");
                var manager2 = Manager("Quality");
                var levelProperty = QualityLevelIn(manager2, tab);
                var property = Relative(levelProperty, entry.path);
                ComponentService.SetProperty(property, entry.value);
                manager2.ApplyModifiedProperties();
                changes.Add(AssetViews.Label(property) + " = " + AssetViews.Printable(AssetViews.Display(property)));
            }
        }

        private static SerializedProperty QualityLevelIn(SerializedObject manager, string name)
        {
            var levels = manager.FindProperty("m_QualitySettings");
            for (var index = 0; index < levels.arraySize; index++)
                if (AssetViews.Normalize(levels.GetArrayElementAtIndex(index).FindPropertyRelative("name").stringValue) == AssetViews.Normalize(name))
                    return levels.GetArrayElementAtIndex(index);
            throw new ArgumentException("Quality level was not found: " + name);
        }

        private static string AddQualityLevel(string name)
        {
            if (QualitySettings.names.Any(level => AssetViews.Normalize(level) == AssetViews.Normalize(name)))
                throw new InvalidOperationException("Quality level already exists: " + name);
            var manager = Manager("Quality");
            var levels = manager.FindProperty("m_QualitySettings");
            levels.InsertArrayElementAtIndex(levels.arraySize - 1);
            levels.GetArrayElementAtIndex(levels.arraySize - 1).FindPropertyRelative("name").stringValue = name;
            manager.ApplyModifiedProperties();
            return Saved("Quality level added: " + name + " (copy of " + QualitySettings.names[QualitySettings.names.Length - 2] + ")", "Quality");
        }

        private static string RemoveQualityLevel(string name)
        {
            var index = QualityIndex(name);
            if (QualitySettings.names.Length == 1)
                throw new InvalidOperationException("The last quality level cannot be removed.");
            var manager = Manager("Quality");
            manager.FindProperty("m_QualitySettings").DeleteArrayElementAtIndex(index);
            manager.ApplyModifiedProperties();
            return Saved("Quality level removed: " + name, "Quality");
        }

        // ---------- Graphics ----------

        private static readonly string[] PipelineTabs = { "URP", "HDRP" };

        private static Type PipelineAssetType(string tab)
        {
            var name = AssetViews.Normalize(tab) == "urp" || AssetViews.Normalize(tab) == "universal"
                ? "UnityEngine.Rendering.Universal.UniversalRenderPipeline"
                : AssetViews.Normalize(tab) == "hdrp" || AssetViews.Normalize(tab) == "highdefinition"
                    ? "UnityEngine.Rendering.HighDefinition.HDRenderPipeline"
                    : null;
            var type = name == null ? null : FindType(name);
            if (type == null)
                throw new ArgumentException("Graphics tab was not found: " + tab + ". Tabs: " + string.Join(", ", InstalledPipelineTabs()));
            return type;
        }

        private static IEnumerable<string> InstalledPipelineTabs()
        {
            return PipelineTabs.Where(tab =>
            {
                try { return GlobalSettings(PipelineAssetType(tab)) != null; }
                catch (ArgumentException) { return false; }
            });
        }

        private static RenderPipelineGlobalSettings GlobalSettings(Type pipeline)
        {
            return UnityEditor.Rendering.EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset(pipeline);
        }

        private static IEnumerable<SerializedProperty> GlobalEntries(RenderPipelineGlobalSettings settings)
        {
            var list = new SerializedObject(settings).FindProperty("m_Settings.m_SettingsList.m_List");
            for (var index = 0; list != null && index < list.arraySize; index++)
                yield return list.GetArrayElementAtIndex(index);
        }

        private static string Category(Type type)
        {
            var attribute = type.GetCustomAttributes(true).FirstOrDefault(item => item.GetType().Name == "CategoryInfoAttribute");
            var name = attribute == null ? null : attribute.GetType().GetProperty("Name") != null
                ? attribute.GetType().GetProperty("Name").GetValue(attribute) as string
                : attribute.GetType().GetField("Name").GetValue(attribute) as string;
            return string.IsNullOrEmpty(name) ? ObjectNames.NicifyVariableName(type.Name) : name;
        }

        // Runtime resources (shaders, textures) are not shown by the Graphics tab either.
        private static bool Hidden(Type type)
        {
            return type.GetCustomAttributes(typeof(HideInInspector), true).Length > 0 || Category(type).StartsWith("R:", StringComparison.Ordinal) ||
                typeof(IRenderPipelineResources).IsAssignableFrom(type);
        }

        private sealed class GraphicsField
        {
            internal string category;
            internal string label;
            internal SerializedProperty property;
        }

        // A settings type with one field is shown under the type's name ("Look Dev Volume Profile", not "Volume Profile").
        private static List<GraphicsField> GraphicsFields(SerializedObject serialized)
        {
            var fields = new List<GraphicsField>();
            var list = serialized.FindProperty("m_Settings.m_SettingsList.m_List");
            for (var index = 0; list != null && index < list.arraySize; index++)
            {
                var item = list.GetArrayElementAtIndex(index);
                var value = item.managedReferenceValue;
                if (value == null || Hidden(value.GetType()))
                    continue;
                var category = Category(value.GetType());
                var typeLabel = ObjectNames.NicifyVariableName(value.GetType().Name);
                if (typeLabel.EndsWith(" Settings", StringComparison.Ordinal))
                    typeLabel = typeLabel.Substring(0, typeLabel.Length - " Settings".Length);
                var children = AssetViews.ChildProperties(item).ToList();
                foreach (var child in children)
                {
                    var label = AssetViews.Label(child);
                    if (children.Count == 1 && typeLabel.EndsWith(label, StringComparison.OrdinalIgnoreCase) ||
                        fields.Any(field => field.category == category && field.label == label))
                        label = typeLabel.EndsWith(label, StringComparison.OrdinalIgnoreCase) ? typeLabel : typeLabel + " " + label;
                    fields.Add(new GraphicsField { category = category, label = label, property = child });
                }
            }
            return fields;
        }

        // "Category.Field" or just "Field" when it is unique in the tab.
        private static List<GraphicsField> MatchingFields(List<GraphicsField> fields, string key)
        {
            var separator = key.IndexOf('.');
            if (separator > 0)
            {
                var scoped = fields.Where(field => AssetViews.KeyIs(key.Substring(0, separator), field.category)).ToList();
                if (scoped.Count > 0)
                {
                    fields = scoped;
                    key = key.Substring(separator + 1);
                }
            }
            return fields.Where(field => AssetViews.KeyIs(key, field.label, field.property.name)).ToList();
        }

        private static object GraphicsValue(SerializedProperty property)
        {
            return property.type == "FrameSettings" ? FrameSettingsText.Off(property) : AssetViews.Display(property);
        }

        // "Camera.ScreenSpaceReflection=false" (or the field name, Camera.SSR) toggles one Frame Settings checkbox.
        private static string SetFrameSetting(GraphicsField owner, string name, bool on)
        {
            var field = FrameSettingsText.Find(name);
            FrameSettingsText.Set(owner.property, field, on);
            return owner.label + "." + FrameSettingsText.Label(field) + " = " + (on ? "true" : "false");
        }

        private static JsonText Graphics(string tab, string property)
        {
            if (tab.Length == 0)
            {
                var result = AssetViews.Fields(Manager("Graphics"), item => item.name == "m_CustomRenderPipeline" || !item.isArray && item.propertyType != SerializedPropertyType.Generic);
                result.Add("Pipeline Tabs", InstalledPipelineTabs().ToArray());
                return result;
            }
            var settings = GlobalSettings(PipelineAssetType(tab));
            if (settings == null)
                throw new InvalidOperationException("The " + tab + " tab has no Global Settings asset.");
            var categories = new JsonText().Add("Global Settings Asset", AssetDatabase.GetAssetPath(settings));
            foreach (var group in GraphicsFields(new SerializedObject(settings)).GroupBy(field => field.category))
            {
                if (!string.IsNullOrEmpty(property) && !AssetViews.KeyIs(property, group.Key))
                    continue;
                var fields = new JsonText();
                foreach (var field in group)
                    fields.Add(field.label, GraphicsValue(field.property));
                categories.Add(group.Key, fields);
            }
            return categories;
        }

        private static void ModifyGraphics(string tab, PropertyValue[] values, List<string> changes)
        {
            if (tab.Length == 0)
            {
                ModifyManager("Graphics", values, changes, null);
                return;
            }
            var settings = GlobalSettings(PipelineAssetType(tab));
            var serialized = new SerializedObject(settings);
            var fields = GraphicsFields(serialized);
            foreach (var entry in values)
            {
                var matches = MatchingFields(fields, entry.path);
                var dot = entry.path.LastIndexOf('.');
                if (matches.Count == 0 && dot > 0)
                {
                    var owners = MatchingFields(fields, entry.path.Substring(0, dot)).Where(field => field.property.type == "FrameSettings").ToList();
                    if (owners.Count == 1)
                    {
                        changes.Add(SetFrameSetting(owners[0], entry.path.Substring(dot + 1), AssetViews.Bool(entry.value)));
                        serialized.ApplyModifiedProperties();
                        continue;
                    }
                }
                if (matches.Count != 1)
                    throw new ArgumentException(matches.Count == 0
                        ? "Graphics " + tab + " setting was not found: " + entry.path
                        : "Graphics " + tab + " setting is ambiguous; use <Category>.<Field>: " + entry.path);
                ComponentService.SetProperty(matches[0].property, entry.value);
                serialized.ApplyModifiedProperties();
                changes.Add(matches[0].label + " = " + AssetViews.Printable(GraphicsValue(matches[0].property)));
            }
            EditorUtility.SetDirty(settings);
        }

        // ---------- Script Execution Order ----------

        private static JsonText ExecutionOrder()
        {
            var result = new JsonText();
            foreach (var script in MonoImporter.GetAllRuntimeMonoScripts()
                .Where(script => script != null && script.GetClass() != null && MonoImporter.GetExecutionOrder(script) != 0)
                .OrderBy(script => MonoImporter.GetExecutionOrder(script)))
                result.Add(script.GetClass().FullName, MonoImporter.GetExecutionOrder(script));
            return result.Count == 0 ? new JsonText().Add("Default Time", "all scripts") : result;
        }

        private static string SetExecutionOrder(string className, int order)
        {
            var script = MonoImporter.GetAllRuntimeMonoScripts().FirstOrDefault(candidate => candidate != null && candidate.GetClass() != null &&
                (candidate.GetClass().FullName == className || candidate.GetClass().Name == className));
            if (script == null)
                throw new ArgumentException("Script class was not found: " + className);
            MonoImporter.SetExecutionOrder(script, order);
            return script.GetClass().FullName + " = " + order;
        }

        // ---------- shared ----------

        private static void ModifyManager(string section, PropertyValue[] values, List<string> changes, Func<string, string, bool> special)
        {
            var manager = Manager(section);
            foreach (var entry in values)
            {
                if (special != null && special(entry.path, entry.value))
                {
                    changes.Add(entry.path + " = " + AssetViews.Text(entry.value));
                    manager.Update();
                    continue;
                }
                var property = AssetViews.FindByLabel(manager, entry.path);
                if (property == null)
                    throw new ArgumentException(section + " setting was not found: " + entry.path);
                ComponentService.SetProperty(property, entry.value);
                manager.ApplyModifiedProperties();
                changes.Add(AssetViews.Label(property) + " = " + AssetViews.Printable(AssetViews.Display(property)));
            }
            EditorUtility.SetDirty(manager.targetObject);
        }

        private static SerializedProperty Relative(SerializedProperty parent, string key)
        {
            var child = parent.Copy();
            var end = parent.GetEndProperty();
            var enter = true;
            while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
            {
                enter = false;
                if (AssetViews.Normalize(child.displayName) == AssetViews.Normalize(key) || AssetViews.Normalize(child.name) == AssetViews.Normalize(key))
                    return child;
            }
            throw new ArgumentException("Setting was not found: " + key);
        }

        private static string Saved(string message, string section)
        {
            EditorUtility.SetDirty(Manager(section).targetObject);
            AssetDatabase.SaveAssets();
            return message;
        }

        private static Type FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(type => type != null);
        }
    }
}
