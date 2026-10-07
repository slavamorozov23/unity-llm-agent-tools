using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAgentBridge.Editor
{
    // Shader Graph window: nodes by short id ("a3f2 Multiply"), their inputs as values or "← node.slot",
    // Blackboard properties, master stack blocks and the errors shown as node badges.
    // Edits go through the same graph calls as the window and are saved, then the edited element is framed.
    [InitializeOnLoad]
    internal sealed partial class ShaderGraphView : AssetView
    {
        private const int PageSize = 60;

        static ShaderGraphView()
        {
            AssetViews.Register(new ShaderGraphView());
            CommandProcessor.BeforeCommand += ShaderGraphWindow.OnCommand;
            CommandProcessor.ShaderGraphPreview = Preview;
            ShaderGraphTemplates.Register();
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return path != null && (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase));
        }

        internal override bool OwnsWindow { get { return true; } }

        // ---------- read ----------

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            var window = ShaderGraphWindow.Open(path);
            var graph = ShaderGraphWindow.Graph(window);
            var model = new Model(graph);
            if (!string.IsNullOrEmpty(property))
                return DescribePart(window, model, property);
            var result = new JsonText().Add("Graph Settings", GraphSettings(window, graph, false));
            var properties = Properties(graph);
            result.AddIf(properties.Count > 0, "Properties", properties);
            result.Add("Nodes", NodesPage(model, 1, result));
            foreach (var stage in Stages(graph))
                result.Add(stage.Key, StageBlocks(model, stage.Value));
            var groups = new JsonText();
            foreach (var group in Sg.Items(Sg.Get(graph, "groups")))
                groups.Add((string)Sg.Get(group, "title"), model.nodes.Where(node => Sg.Get(node, "group") == group).Select(model.Id).ToArray());
            result.AddIf(groups.Count > 0, "Groups", groups);
            var stickies = new JsonText();
            foreach (var note in Sg.Items(Sg.Get(graph, "stickyNotes")))
                stickies.Add((string)Sg.Get(note, "title"), (string)Sg.Get(note, "content"));
            result.AddIf(stickies.Count > 0, "Sticky Notes", stickies);
            var errors = Errors(model, path, window);
            result.AddIf(errors.Count > 0, "Errors", errors);
            ShaderGraphWindow.Frame(window, Enumerable.Empty<object>());
            return result;
        }

        private static JsonText DescribePart(EditorWindow window, Model model, string key)
        {
            var graph = model.graph;
            if (AssetViews.KeyIs(key, "Code") || key.StartsWith("Code/", StringComparison.OrdinalIgnoreCase))
            {
                var title = key.Length > 5 ? key.Substring(5) : null;
                var scope = title == null ? null : FindGroup(graph, title);
                if (title != null && scope == null)
                    throw new ArgumentException("Group was not found: " + title + ". Groups: " + string.Join(", ", Sg.Items(Sg.Get(graph, "groups")).Select(item => GroupTitle(graph, item))));
                ShaderGraphWindow.Frame(window, scope == null ? Enumerable.Empty<object>() : model.nodes.Where(node => Sg.Get(node, "group") == scope), scope);
                return new JsonText().Add("Code", GraphCode(model, scope));
            }
            if (key.StartsWith("Nodes/", StringComparison.OrdinalIgnoreCase))
            {
                var result = new JsonText();
                return result.Add("Nodes", NodesPage(model, AssetViews.Int(key.Substring(6)), result));
            }
            if (AssetViews.KeyIs(key, "Graph Settings"))
                return new JsonText().Add("Graph Settings", GraphSettings(window, graph, true));
            foreach (var stage in Stages(graph))
                if (AssetViews.KeyIs(key, stage.Key))
                    return new JsonText().Add(stage.Key, StageBlocks(model, stage.Value));
            var input = FindInput(graph, key, false);
            if (input != null)
            {
                ShaderGraphWindow.ShowInBlackboard(window, input);
                return new JsonText().Add(InputLabel(input), InputDetails(input));
            }
            var group = Sg.Items(Sg.Get(graph, "groups")).FirstOrDefault(item => AssetViews.KeyIs(key, (string)Sg.Get(item, "title")));
            if (group != null)
            {
                var members = model.nodes.Where(node => Sg.Get(node, "group") == group).ToList();
                var nodes = new JsonText();
                foreach (var node in members)
                    nodes.Add(model.Label(node), NodeDetails(model, node));
                ShaderGraphWindow.Frame(window, members, group);
                return new JsonText().Add((string)Sg.Get(group, "title"), nodes);
            }
            var found = model.Find(key);
            ShaderGraphWindow.Frame(window, new[] { found });
            return new JsonText().Add(model.Label(found), NodeDetails(model, found));
        }

        private static JsonText NodesPage(Model model, int page, JsonText owner)
        {
            var nodes = model.nodes.Where(node => !model.IsBlock(node)).ToList();
            var result = new JsonText();
            foreach (var node in nodes.Skip((page - 1) * PageSize).Take(PageSize))
                result.Add(model.Label(node), NodeSummary(model, node));
            if (nodes.Count > page * PageSize)
                owner.Add("More Nodes", (nodes.Count - page * PageSize) + " (--property Nodes/" + (page + 1) + ")");
            return result;
        }

        // Inputs: an upstream "← id.slot" or the value typed into the port; node settings follow.
        private static JsonText NodeSummary(Model model, object node)
        {
            var result = new JsonText();
            foreach (var slot in model.Inputs(node))
            {
                var value = model.InputText(node, slot);
                if (value != null)
                    result.Add(SlotName(slot), value);
            }
            foreach (var control in Controls(node))
                result.AddIf(ControlValue(node, control) != null, control.label, ControlValue(node, control));
            if (Sg.Has(node, "property") && Sg.Get(node, "property") != null)
                result.Add("Property", (string)Sg.Get(Sg.Get(node, "property"), "displayName"));
            return result;
        }

        // The node as the graph now wires it: inputs, then outputs with their resolved type and targets.
        private static string NodeLine(Model model, object node)
        {
            var inputs = model.Inputs(node).Select(slot => SlotName(slot) + "=" + (model.InputText(node, slot) ?? "-"));
            var outputs = model.Outputs(node).Select(slot => SlotName(slot) + "(" + TypeName(slot) + ")" +
                string.Concat(model.OutputTargets(node, slot).Select(target => " " + target)));
            // Dynamic inputs take one size from their wires, as their ports show it.
            var dynamic = model.Inputs(node).FirstOrDefault(slot => slot.GetType().Name.StartsWith("Dynamic", StringComparison.Ordinal) && VectorSize(slot) > 0);
            return model.Label(node) + (dynamic == null ? string.Empty : " [" + TypeName(dynamic) + "]") + ": " +
                string.Join("; ", new[] { string.Join(", ", inputs), string.Join(", ", outputs) }.Where(part => part.Length > 0));
        }

        // A dynamic port takes the size of what is connected; typed components beyond it are dropped, as the port shows.
        private static IEnumerable<string> Truncations(Model model, IEnumerable<object> nodes)
        {
            foreach (var node in nodes.Where(model.nodes.Contains).Where(item => !model.IsBlock(item)).Distinct())
            foreach (var slot in model.Inputs(node))
            {
                var typed = model.IncomingEdges(node, slot).Any() || !Sg.Has(slot, "value") ? null : Sg.Get(slot, "value");
                if (!(typed is Vector4) && !(typed is Matrix4x4))
                    continue;
                var value = typed is Matrix4x4 ? ((Matrix4x4)typed).GetRow(0) : (Vector4)typed;
                var size = VectorSize(slot);
                if (size < 1 || size > 3 || !Enumerable.Range(size, 4 - size).Any(index => Mathf.Abs(value[index]) > 1e-6f))
                    continue;
                // A number typed into a vector input fills every component; nothing of it is lost.
                if (Enumerable.Range(1, 3).All(index => Mathf.Abs(value[index] - value[0]) <= 1e-6f))
                    continue;
                var dynamic = slot.GetType().Name.StartsWith("Dynamic", StringComparison.Ordinal);
                var typedSize = Enumerable.Range(0, 4).Last(index => index == 0 || Mathf.Abs(value[index]) > 1e-6f) + 1;
                yield return "Warning: " + model.Label(node) + " works in " + TypeName(slot) + (dynamic ? " until a vector reaches its inputs" : string.Empty) + ", " + SlotName(slot) + "=" +
                    AssetViews.Numbers(Enumerable.Range(0, typedSize).Select(index => value[index]).ToArray()) +
                    " keeps " + AssetViews.Numbers(Enumerable.Range(0, size).Select(index => value[index]).ToArray());
            }
        }

        private static readonly string[] PreviewModes = { "Inherit", "Preview 2D", "Preview 3D" };

        // true|false as the node's fold arrow; a chosen mode shows as the Graph Inspector names it.
        private static object PreviewText(object node)
        {
            var expanded = (bool)Sg.Get(node, "previewExpanded");
            var mode = Sg.Get(node, "m_PreviewMode").ToString();
            return !expanded || mode == "Inherit" ? (object)expanded : mode.Replace("Preview", "Preview ");
        }

        private static string TypeName(object slot)
        {
            var type = SlotType(slot);
            return type == "Vector1" ? "Float" : type;
        }

        private static JsonText NodeDetails(Model model, object node)
        {
            var result = new JsonText();
            var inputs = new JsonText();
            foreach (var slot in model.Inputs(node))
                inputs.Add(SlotName(slot) + " (" + SlotType(slot) + ")", model.InputText(node, slot) ?? "-");
            result.AddIf(inputs.Count > 0, "Inputs", inputs);
            var outputs = new JsonText();
            foreach (var slot in model.Outputs(node))
                outputs.Add(SlotName(slot) + " (" + SlotType(slot) + ")", model.OutputTargets(node, slot));
            result.AddIf(outputs.Count > 0, "Outputs", outputs);
            foreach (var control in Controls(node))
                result.AddIf(ControlValue(node, control) != null, control.label, ControlValue(node, control));
            if ((bool)Sg.Get(node, "hasPreview"))
                result.Add("Preview", PreviewText(node));
            var position = ((Rect)Sg.Get(Sg.Get(node, "drawState"), "position")).position;
            result.Add("Position", AssetViews.Numbers(position.x, position.y));
            var group = Sg.Get(node, "group");
            if (group != null)
                result.Add("Group", (string)Sg.Get(group, "title"));
            var errors = model.NodeErrors(node);
            result.AddIf(errors.Count > 0, "Errors", errors);
            result.AddIf(SourceChanged(node) != null, "Warning", SourceChanged(node) + " changed on disk after Unity imported it; run compile");
            return result;
        }

        private static JsonText StageBlocks(Model model, object context)
        {
            var result = new JsonText();
            foreach (var reference in Sg.Items(Sg.Get(context, "blocks")))
            {
                var block = Sg.Get(reference, "value");
                var slot = model.Inputs(block).FirstOrDefault();
                result.Add(model.Label(block), slot == null ? "-" : model.InputText(block, slot) ?? "-");
            }
            return result;
        }

        private static IEnumerable<KeyValuePair<string, object>> Stages(object graph)
        {
            if ((bool)Sg.Get(graph, "isSubGraph"))
            {
                yield break;
            }
            yield return new KeyValuePair<string, object>("Vertex", Sg.Get(graph, "vertexContext"));
            yield return new KeyValuePair<string, object>("Fragment", Sg.Get(graph, "fragmentContext"));
        }

        // ---------- Blackboard ----------

        private static JsonText Properties(object graph)
        {
            var result = new JsonText();
            foreach (var input in Inputs(graph))
                result.Add(InputLabel(input), InputSummary(input));
            return result;
        }

        private static IEnumerable<object> Inputs(object graph)
        {
            return Sg.Items(Sg.Get(graph, "properties")).Concat(Sg.Items(Sg.Get(graph, "keywords"))).Concat(Sg.Items(Sg.Get(graph, "dropdowns")));
        }

        private static string InputLabel(object input)
        {
            return (string)Sg.Get(input, "displayName") + " (" + (string)Sg.Get(input, "referenceName") + ")";
        }

        private static string InputKind(object input)
        {
            if (Sg.Has(input, "propertyType"))
                return Sg.Get(input, "propertyType").ToString();
            return Sg.Has(input, "keywordType") ? Sg.Get(input, "keywordType") + " Keyword" : "Dropdown";
        }

        private static string InputSummary(object input)
        {
            var value = Sg.Has(input, "propertyType") ? Printable(Sg.Get(input, "value")) : Sg.Has(input, "value") ? Printable(Sg.Get(input, "value")) : null;
            return InputKind(input) + (value == null ? string.Empty : " = " + value) + ((bool)Sg.Get(input, "generatePropertyBlock") ? string.Empty : " (hidden)");
        }

        private static JsonText InputDetails(object input)
        {
            var result = new JsonText()
                .Add("Type", InputKind(input))
                .Add("Name", (string)Sg.Get(input, "displayName"))
                .Add("Reference", (string)Sg.Get(input, "referenceName"))
                .Add("Show In Inspector", (bool)Sg.Get(input, "generatePropertyBlock"));
            if (Sg.Has(input, "value"))
                result.Add("Default", Printable(Sg.Get(input, "value")));
            foreach (var property in EditableMembers(input))
                if (!result.Contains(property.Key))
                    result.Add(property.Key, Printable(property.Value.GetValue(input, null)));
            return result;
        }

        // Settings the property Inspector shows besides name, reference and default (Mode, Precision, Scope, HDR...).
        private static IEnumerable<KeyValuePair<string, PropertyInfo>> EditableMembers(object input)
        {
            var names = new[] { "floatType", "rangeValues", "precision", "hlslDeclarationOverride", "overrideHLSLDeclaration", "isHidden", "colorMode", "defaultType", "useTilingAndOffset", "isMainTexture", "isMainColor", "keywordDefinition", "keywordScope", "keywordStages" };
            foreach (var name in names)
            {
                var property = input.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.GetGetMethod(true) != null && property.GetSetMethod(true) != null)
                    yield return new KeyValuePair<string, PropertyInfo>(ObjectNames.NicifyVariableName(name), property);
            }
        }

        private static object FindInput(object graph, string key, bool required)
        {
            var input = Inputs(graph).FirstOrDefault(item => AssetViews.KeyIs(key, (string)Sg.Get(item, "displayName"), (string)Sg.Get(item, "referenceName")));
            if (input == null && required)
                throw new ArgumentException("Blackboard property was not found: " + key + ". Properties: " + string.Join(", ", Inputs(graph).Select(InputLabel)));
            return input;
        }

        // ---------- Graph Settings ----------

        // One row of the Graph Settings tab: its section (target or foldout), its label and the field the window draws.
        private sealed class SettingRow
        {
            internal string section;
            internal string label;
            internal VisualElement field;
        }

        // Graph Settings are built the way the Graph Inspector builds them: every active target draws its rows into
        // the window's own GUI context, with the window's labels. Collapsed sections are opened while reading, as the
        // developer would unfold them, and restored after. A value is set through the row's field, so the field's own
        // callback (undo, blocks added or removed, validation) runs exactly as for a click.
        private static List<SettingRow> SettingRows(EditorWindow window, object graph, VisualElement host)
        {
            var rows = new List<SettingRow>();
            var graphView = ShaderGraphWindow.GraphView(window);
            Action onChange = () => TargetSettingsChanged(graphView, graph);
            Action<string> registerUndo = name => Undo(graph, name);
            foreach (var target in Sg.Items(Sg.Get(graph, "activeTargets")))
            {
                var context = (VisualElement)Activator.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.TargetPropertyGUIContext"),
                    new object[] { (Action)(() => Sg.Call(graph, "ValidateGraph")) });
                var masks = FoldoutMasks(target);
                var unfolded = masks.Select(mask => (int)Sg.Get(mask, "inspectorFoldoutMask")).ToList();
                foreach (var mask in masks)
                    Sg.Set(mask, "inspectorFoldoutMask", -1);
                try
                {
                    var arguments = new object[] { context, onChange, registerUndo };
                    target.GetType().GetMethod("GetPropertiesGUI", BindingFlags.Instance | BindingFlags.Public).Invoke(target, arguments);
                    context = (VisualElement)arguments[0];
                }
                finally
                {
                    for (var index = 0; index < masks.Count; index++)
                        Sg.Set(masks[index], "inspectorFoldoutMask", unfolded[index]);
                }
                host.Add(context);
                var section = (string)Sg.Get(target, "displayName");
                foreach (var child in context.Children())
                {
                    var foldout = child as Foldout;
                    if (foldout != null)
                    {
                        section = foldout.text;
                        continue;
                    }
                    if (child.GetType().Name != "PropertyRow")
                        continue;
                    var label = child.Q<Label>();
                    var field = child.Query<VisualElement>().Where(IsField).First();
                    if (label != null && field != null && !string.IsNullOrWhiteSpace(label.text))
                        rows.Add(new SettingRow { section = section, label = label.text.Trim(), field = field });
                }
            }
            return rows;
        }

        // HDRP keeps which Graph Settings sections are unfolded in a mask on the target's data.
        private static List<object> FoldoutMasks(object target)
        {
            var objects = new List<object> { target };
            if (Sg.Has(target, "m_Datas"))
                objects.AddRange(Sg.Items(Sg.Get(target, "m_Datas")).Select(item => Sg.Has(item, "value") ? Sg.Get(item, "value") : item).Where(item => item != null));
            return objects.Where(item => Sg.Has(item, "inspectorFoldoutMask") && Sg.Get(item, "inspectorFoldoutMask") is int).ToList();
        }

        // What the Graph Inspector does after a target setting changes: stack blocks follow the settings.
        private static void TargetSettingsChanged(object graphView, object graph)
        {
            var activeBlocks = Sg.Call(graph, "GetActiveBlocksForAllActiveTargets");
            if ((bool)Sg.CallStatic(Sg.Type("UnityEditor.ShaderGraph.ShaderGraphPreferences"), "get_autoAddRemoveBlocks"))
                Sg.Call(graph, "AddRemoveBlocksFromActiveList", activeBlocks);
            Sg.Call(graph, "RefreshBadgesAndPreviews");
            Sg.Call(graph, "UpdateActiveBlocks", activeBlocks);
        }

        private static bool IsField(VisualElement element)
        {
            for (var type = element.GetType(); type != null; type = type.BaseType)
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BaseField<>))
                    return true;
            return false;
        }

        private static object FieldValue(VisualElement field)
        {
            return field.GetType().GetProperty("value").GetValue(field, null);
        }

        private static T WithSettingRows<T>(EditorWindow window, object graph, Func<List<SettingRow>, T> use)
        {
            // The rows live on the window's panel while used, so a changed value sends its change event like a click.
            var host = new VisualElement();
            host.style.display = DisplayStyle.None;
            window.rootVisualElement.Add(host);
            try
            {
                return use(SettingRows(window, graph, host));
            }
            finally
            {
                host.RemoveFromHierarchy();
            }
        }

        private static JsonText GraphSettings(EditorWindow window, object graph, bool all)
        {
            var result = new JsonText()
                .Add("Precision", Sg.Get(graph, "graphDefaultPrecision").ToString())
                .Add("Active Targets", Sg.Items(Sg.Get(graph, "activeTargets")).Select(target => (string)Sg.Get(target, "displayName")).ToArray());
            WithSettingRows(window, graph, rows =>
            {
                if (!all)
                {
                    foreach (var row in rows.Where(item => IsHeadline(item.label)))
                        result.Add(result.Contains(row.label) ? row.section + "." + row.label : row.label, FieldText(row.field));
                    if (rows.Count > 0)
                        result.Add("More", "--property \"Graph Settings\"");
                    return 0;
                }
                foreach (var section in rows.GroupBy(row => row.section))
                {
                    var fields = new JsonText();
                    foreach (var row in section)
                        fields.Add(fields.Contains(row.label) ? row.label + " [" + section.TakeWhile(item => item != row).Count(item => item.label == row.label) + "]" : row.label, FieldText(row.field));
                    result.Add(section.Key, fields);
                }
                return 0;
            });
            return result;
        }

        private static bool IsHeadline(string label)
        {
            return new[] { "Material", "Surface Type", "Rendering Pass", "Blending Mode", "Alpha Clipping", "Render Face", "Workflow Mode", "Double-Sided Mode", "Cast Shadows", "Receive Shadows" }
                .Any(item => AssetViews.KeyIs(label, item));
        }

        // "Surface Type", or "Universal.Surface Type" for a label that repeats in a later section.
        private static string SetGraphSetting(EditorWindow window, object graph, string key, string raw)
        {
            if (AssetViews.KeyIs(key, "Precision"))
            {
                Undo(graph, "Change Graph Precision");
                Sg.Call(graph, "SetGraphDefaultPrecision", ParseEnum(Sg.Get(graph, "graphDefaultPrecision").GetType(), raw));
                return "Graph Settings.Precision = " + Sg.Get(graph, "graphDefaultPrecision");
            }
            return WithSettingRows(window, graph, rows =>
            {
                var dot = key.LastIndexOf('.');
                var section = dot > 0 ? key.Substring(0, dot) : null;
                var label = dot > 0 ? key.Substring(dot + 1) : key;
                // As asset-info shows them: the label alone is its first row, the others carry their section.
                var matches = rows.Where(row => AssetViews.KeyIs(label, row.label) && (section == null || AssetViews.KeyIs(section, row.section))).Take(1).ToList();
                if (matches.Count == 0)
                    throw new ArgumentException("Graph setting was not found: " + key + ". Settings: " + string.Join(", ", rows.Select(row => row.label).Distinct()));
                var row = matches[0];
                if (!row.field.enabledInHierarchy)
                    throw new InvalidOperationException(row.label + " is disabled in Graph Settings for the current values.");
                var property = row.field.GetType().GetProperty("value");
                property.SetValue(row.field, FieldInput(row.field, property.PropertyType, raw), null);
                var shown = rows.First(item => item.label == row.label) == row ? row.label : row.section + "." + row.label;
                return "Graph Settings." + shown + " = " + FieldText(row.field);
            });
        }

        // The value as the row shows it: a popup shows its own text ("After post-process"), not the enum name.
        private static string FieldText(VisualElement field)
        {
            var value = FieldValue(field);
            if (value is Enum || Sg.Has(field, "choices"))
            {
                var text = field.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public)?.GetValue(field, null) as string;
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            return Printable(value);
        }

        // Values as typed into the field: enum names as shown, popup choices by their text or name.
        private static object FieldInput(VisualElement field, Type type, string raw)
        {
            if (type == typeof(Enum))
                type = FieldValue(field).GetType();
            if (Sg.Has(field, "choices"))
            {
                var choices = Sg.Items(Sg.Get(field, "choices")).ToList();
                var format = field.GetType().GetProperty("formatListItemCallback", BindingFlags.Instance | BindingFlags.Public)?.GetValue(field, null) as Delegate;
                Func<object, string> label = item => format != null ? (string)format.DynamicInvoke(item) : Printable(item);
                var choice = choices.FirstOrDefault(item => AssetViews.KeyIs(raw, label(item), Convert.ToString(item, CultureInfo.InvariantCulture)));
                if (choice == null)
                    throw new ArgumentException("Choices: " + string.Join(", ", choices.Select(label)));
                return choice;
            }
            return Parse(type, raw);
        }

        // ---------- modify ----------

        private static PropertyValue[] ModifyGraph(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            var window = ShaderGraphWindow.Open(path);
            var graph = ShaderGraphWindow.Graph(window);
            var structure = Structure(graph);
            var touched = new List<object>();
            object shownInput = null;
            var settings = false;
            var defaults = AssetService.FloatDefaults(path);
            var stages = Stages(graph).Select(stage => stage.Key).ToArray();
            foreach (var entry in values)
            {
                var model = new Model(graph);
                if (AssetViews.KeyIs(entry.path, "Code"))
                {
                    changes.AddRange(WriteCode(window, graph, AssetViews.Text(entry.value), touched));
                    continue;
                }
                // Blocks as asset-info lists them under their stage: "Fragment.Base Color" is the Base Color block.
                var stage = stages.FirstOrDefault(name => entry.path.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase));
                if (stage != null && model.TryFind(entry.path.Substring(stage.Length + 1)) != null)
                    entry.path = entry.path.Substring(stage.Length + 1);
                var parts = entry.path.Split('.');
                if (parts.Length >= 2 && AssetViews.KeyIs(parts[0], "Graph Settings", "Settings"))
                {
                    changes.Add(SetGraphSetting(window, graph, string.Join(".", parts.Skip(1)), AssetViews.Text(entry.value)));
                    settings = true;
                    continue;
                }
                var input = FindInput(graph, parts[0], false);
                if (input != null && parts.Length == 2)
                {
                    changes.Add(SetInputField(window, graph, input, parts[1], AssetViews.Text(entry.value)));
                    shownInput = input;
                    continue;
                }
                // Settings as asset-info prints them: "Surface Type" or "Surface Options.Surface Type".
                if (input == null && model.TryFind(entry.path) == null && model.TryFind(parts[0]) == null)
                {
                    try
                    {
                        changes.Add(SetGraphSetting(window, graph, entry.path, AssetViews.Text(entry.value)));
                        settings = true;
                    }
                    catch (ArgumentException error) when (error.Message.StartsWith("Graph setting was not found", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Node or graph setting was not found: " + entry.path);
                    }
                    continue;
                }
                changes.Add(SetNodeField(window, model, entry.path, AssetViews.Text(entry.value), touched));
            }
            Finish(window, path, structure, touched, shownInput, changes);
            changes.AddRange(Truncations(new Model(ShaderGraphWindow.Graph(window)), touched));
            if (settings)
                changes.AddRange(AssetService.MaterialsKeeping(path, defaults).Select(item => "Warning: " + item + "; set it with material-modify"));
            return Array.Empty<PropertyValue>();
        }

        // "<node>.<input>=value|<node>.<output>|None", "<node>.<setting>=value", "<node>.Preview|Position", "<block>=...".
        private static string SetNodeField(EditorWindow window, Model model, string key, string raw, List<object> touched)
        {
            var graph = model.graph;
            raw = WithoutArrow(raw);
            var dot = key.LastIndexOf('.');
            object node;
            string field;
            if (dot < 0)
            {
                node = model.Find(key);
                if (!model.IsBlock(node))
                    throw new ArgumentException("Use <node>.<input>; only blocks take a value directly: " + key);
                field = SlotName(model.Inputs(node).First());
            }
            else
            {
                node = model.Find(key.Substring(0, dot));
                field = key.Substring(dot + 1);
            }
            touched.Add(node);
            var label = dot < 0 ? model.Label(node) : model.Label(node) + "." + field;
            if (AssetViews.KeyIs(field, "Preview"))
            {
                // The Graph Inspector's Preview row: Preview 2D shows a texture flat, as the node preview of a UV chain does.
                var mode = PreviewModes.FirstOrDefault(item => AssetViews.KeyIs(raw, item, item.Replace("Preview ", string.Empty)));
                if (mode != null)
                {
                    Undo(graph, "Change preview");
                    Sg.Set(node, "m_PreviewMode", Sg.Enum("UnityEditor.ShaderGraph.PreviewMode", mode.Replace(" ", string.Empty)));
                    Sg.Set(node, "previewExpanded", true);
                    Sg.Call(node, "Dirty", Sg.Enum("UnityEditor.Graphing.ModificationScope", "Graph"));
                    return label + " = " + AssetViews.Printable(PreviewText(node));
                }
                Undo(graph, "Toggle Preview");
                Sg.Set(node, "previewExpanded", AssetViews.Bool(raw));
                return label + " = " + AssetViews.Printable(Sg.Get(node, "previewExpanded"));
            }
            if (AssetViews.KeyIs(field, "Position"))
            {
                var numbers = Numbers(raw, 2);
                MoveNode(graph, node, new Vector2(numbers[0], numbers[1]));
                return label + " = " + raw;
            }
            var slot = model.Inputs(node).FirstOrDefault(item => AssetViews.KeyIs(field, SlotName(item), Sg.Get(item, "id").ToString()));
            if (slot != null)
            {
                if (AssetViews.KeyIs(raw, "None", "Disconnect"))
                {
                    Disconnect(graph, model.IncomingEdges(node, slot));
                    return label + " = " + (model.InputText(node, slot) ?? "-");
                }
                var source = SplitSlotReference(model, raw) ?? PropertyNode(window, model, raw, node);
                if (source != null)
                {
                    // The window refuses a wire between incompatible ports; so does the command.
                    if (!(bool)Sg.Call(source.Value.Value, "IsCompatibleWith", slot))
                        throw new ArgumentException(model.Label(source.Value.Key) + "." + SlotName(source.Value.Value) + " (" + SlotType(source.Value.Value) +
                            ") cannot connect to " + label + " (" + SlotType(slot) + ").");
                    Connect(graph, source.Value.Key, source.Value.Value, node, slot);
                    touched.Add(source.Value.Key);
                }
                else
                {
                    Disconnect(graph, model.IncomingEdges(node, slot));
                    SetSlotValue(graph, node, slot, raw);
                }
                return label + " = " + (new Model(graph).InputText(node, slot) ?? "-");
            }
            var control = Controls(node).FirstOrDefault(item => AssetViews.KeyIs(field, item.label, item.property.Name));
            if (control.property != null)
            {
                Undo(graph, "Change " + control.label);
                object value;
                if (control.field != null)
                {
                    value = control.property.GetValue(node, null);
                    control.field.SetValue(value, Parse(control.field.FieldType, raw));
                }
                else
                    value = control.property.Name == "functionSource" ? SourceGuid(raw) :
                        control.property.PropertyType.Name == "PopupList" ? PopupChoice(control.label, control.property.GetValue(node, null), raw) : Parse(control.property.PropertyType, raw);
                control.property.SetValue(node, value, null);
                Sg.Call(node, "Dirty", Sg.Enum("UnityEditor.Graphing.ModificationScope", "Graph"));
                return model.Label(node) + "." + control.label + " = " + Printable(ControlValue(node, control));
            }
            if (node.GetType().Name == "CustomFunctionNode" && AssetViews.KeyIs(field, "Inputs", "Outputs"))
                return model.Label(node) + "." + field + " = " + SetSlotList(graph, node, AssetViews.KeyIs(field, "Inputs"), raw);
            throw new ArgumentException(model.Label(node) + " inputs: " + string.Join(", ", model.Inputs(node).Select(SlotName)) +
                "; settings: " + string.Join(", ", Controls(node).Select(item => item.label).Concat(new[] { "Preview", "Position" })));
        }

        // A value that names an output ("a3f2.Out", "Tint.Out") is a connection, like dragging a wire.
        private static KeyValuePair<object, object>? SplitSlotReference(Model model, string raw)
        {
            raw = WithoutArrow(raw);
            var dot = raw.LastIndexOf('.');
            if (dot <= 0 || float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || raw.Contains(","))
                return null;
            var node = model.TryFind(raw.Substring(0, dot));
            if (node == null)
                return null;
            var slot = model.Outputs(node).FirstOrDefault(item => AssetViews.KeyIs(raw.Substring(dot + 1), SlotName(item), Sg.Get(item, "id").ToString()));
            if (slot == null)
                throw new ArgumentException(model.Label(node) + " outputs: " + string.Join(", ", model.Outputs(node).Select(SlotName)));
            return new KeyValuePair<object, object>(node, slot);
        }

        // An input as asset-info prints it: "← a356.Haze" is the same wire as "a356.Haze".
        private static string WithoutArrow(string raw)
        {
            return raw != null && raw.StartsWith("←", StringComparison.Ordinal) ? raw.Substring(1).Trim() : raw;
        }

        // A Blackboard property as a value ("Tint" or "Tint.Out") is dragged in: its node is reused or placed left of the target.
        private static KeyValuePair<object, object>? PropertyNode(EditorWindow window, Model model, string raw, object target)
        {
            var name = raw.EndsWith(".Out", StringComparison.OrdinalIgnoreCase) ? raw.Substring(0, raw.Length - 4) : raw;
            var input = FindInput(model.graph, name, false);
            if (input == null)
                return null;
            var node = model.nodes.FirstOrDefault(item => Sg.Has(item, "property") && Sg.Get(item, "property") == input);
            if (node == null)
            {
                var provider = Sg.Get(ShaderGraphWindow.EditorView(window), "m_SearchWindowProvider");
                node = Sg.Call(provider, "CopyNodeForGraph", Sg.Get(FindNodeEntry(window, "Properties/Property: " + Sg.Get(input, "displayName")), "node"));
                PlaceAndAdd(model, node, NodeRect(target).position - new Vector2(220f, 0f));
            }
            return new KeyValuePair<object, object>(node, new Model(model.graph).Outputs(node).First());
        }

        private static void PlaceAndAdd(Model model, object node, Vector2 position)
        {
            // A free spot below anything already there, so new nodes never cover existing ones.
            while (model.nodes.Any(item => NodeRect(item).Overlaps(new Rect(position, new Vector2(200f, 140f)))))
                position.y += 150f;
            var drawState = Sg.Get(node, "drawState");
            Sg.Set(drawState, "position", new Rect(position, Vector2.zero));
            Sg.Set(node, "drawState", drawState);
            Undo(model.graph, "Add " + Sg.Get(node, "name"));
            Sg.Call(model.graph, "AddNode", node, true);
        }

        private static string SetInputField(EditorWindow window, object graph, object input, string field, string raw)
        {
            var store = Sg.Get(ShaderGraphWindow.GraphObject(window), "graphDataStore");
            var label = (string)Sg.Get(input, "displayName");
            if (AssetViews.KeyIs(field, "Name", "Display Name"))
            {
                Dispatch(store, "UnityEditor.ShaderGraph.Drawing.ChangeDisplayNameAction", ("shaderInputReference", input), ("newDisplayNameValue", raw));
                return label + ".Name = " + Sg.Get(input, "displayName");
            }
            if (AssetViews.KeyIs(field, "Reference", "Reference Name"))
            {
                Undo(graph, "Change Reference Name");
                Dispatch(store, "UnityEditor.ShaderGraph.Drawing.ChangeReferenceNameAction", ("shaderInputReference", input), ("newReferenceNameValue", raw));
                return label + ".Reference = " + Sg.Get(input, "referenceName");
            }
            if (AssetViews.KeyIs(field, "Show In Inspector", "Exposed"))
            {
                Undo(graph, "Change Exposed Toggle");
                Sg.Set(input, "generatePropertyBlock", AssetViews.Bool(raw));
                Sg.Call(graph, "ValidateGraph");
                return label + ".Show In Inspector = " + AssetViews.Printable(Sg.Get(input, "generatePropertyBlock"));
            }
            if (AssetViews.KeyIs(field, "Default", "Value") && Sg.Has(input, "propertyType"))
            {
                Undo(graph, "Change Property Value");
                Dispatch(store, "UnityEditor.ShaderGraph.Drawing.ChangePropertyValueAction", ("shaderInputReference", input), ("newShaderInputValue", PropertyValue(input, raw)));
                return label + ".Default = " + Printable(Sg.Get(input, "value"));
            }
            var member = EditableMembers(input).FirstOrDefault(item => AssetViews.KeyIs(field, item.Key, item.Value.Name));
            if (member.Value == null)
                throw new ArgumentException(label + " fields: Name, Reference, Show In Inspector, Default, " + string.Join(", ", EditableMembers(input).Select(item => item.Key)));
            Undo(graph, "Change " + member.Key);
            member.Value.SetValue(input, Parse(member.Value.PropertyType, raw), null);
            Sg.Call(graph, "ValidateGraph");
            return label + "." + member.Key + " = " + Printable(member.Value.GetValue(input, null));
        }

        // The value ChangePropertyValueAction expects for each property type.
        private static object PropertyValue(object input, string raw)
        {
            var kind = Sg.Get(input, "propertyType").ToString();
            switch (kind)
            {
                case "Boolean":
                    return Activator.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.ToggleData"), AssetViews.Bool(raw));
                case "Float":
                    return Numbers(raw, 1)[0];
                case "Vector2":
                    var v2 = Numbers(raw, 2);
                    return new Vector2(v2[0], v2[1]);
                case "Vector3":
                    var v3 = Numbers(raw, 3);
                    return new Vector3(v3[0], v3[1], v3[2]);
                case "Vector4":
                    var v4 = Numbers(raw, 4);
                    return new Vector4(v4[0], v4[1], v4[2], v4[3]);
                case "Color":
                    var c = Numbers(raw, 4);
                    return new Color(c[0], c[1], c[2], c[3]);
                case "Texture2D":
                case "Texture2DArray":
                case "Texture3D":
                case "Cubemap":
                    return AssetViews.KeyIs(raw, "None") ? null : LoadAsset<Texture>(raw);
                default:
                    throw new NotSupportedException("Default of a " + kind + " property is edited in the Graph Inspector.");
            }
        }

        // ---------- actions ----------

        private static readonly string[] GraphActions = { "Add Node", "Remove Node", "Add Property", "Remove Property", "Add Block", "Remove Block", "Group Selection", "Add Sticky Note", "Remove Sticky Note", "Convert To Nodes" };

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            return GraphActions;
        }

        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            return AsOneStep(path, () => ModifyGraph(path, asset, values, confirm, changes));
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            return AsOneStep(path, () => ExecuteGraph(path, asset, action, values));
        }

        // One Ctrl+Z undoes the whole command; a command that fails halfway leaves the graph as it was.
        private static T AsOneStep<T>(string path, Func<T> command)
        {
            UnityEditor.Undo.IncrementCurrentGroup();
            var group = UnityEditor.Undo.GetCurrentGroup();
            var file = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
            try
            {
                var result = command();
                UnityEditor.Undo.CollapseUndoOperations(group);
                return result;
            }
            catch
            {
                UnityEditor.Undo.RevertAllDownToGroup(group);
                var window = ShaderGraphWindow.Find(path);
                if (window != null)
                {
                    // Nothing was saved yet: the file is the graph as it was.
                    if (file != null && System.IO.File.ReadAllText(path) == file)
                        ShaderGraphWindow.Reload(window);
                    else
                    {
                        ShaderGraphWindow.Sync(window);
                        ShaderGraphWindow.Save(window);
                    }
                }
                throw;
            }
        }

        private static string ExecuteGraph(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            action = AssetViews.Match(GraphActions, action);
            var window = ShaderGraphWindow.Open(path);
            var graph = ShaderGraphWindow.Graph(window);
            var structure = Structure(graph);
            var model = new Model(graph);
            var touched = new List<object>();
            var changes = new List<string>();
            object shownInput = null;
            object framedGroup = null;
            object added = null;
            string result;
            switch (action)
            {
                case "Add Node":
                    added = AddNode(window, model, values, touched);
                    result = "Node added";
                    break;
                case "Remove Node":
                case "Remove Block":
                    var doomed = AssetViews.Value(values, action == "Remove Node" ? "node" : "name").Split(',').Select(item => model.Find(item.Trim())).ToList();
                    // Frame what stays around the removed nodes: their neighbours.
                    touched.AddRange(doomed.SelectMany(item => model.Neighbours(item)).Where(item => !doomed.Contains(item)).Distinct());
                    result = "Removed: " + string.Join(", ", doomed.Select(model.Label));
                    Undo(graph, "Delete Nodes");
                    Sg.Call(graph, "RemoveElements", Sg.Array("UnityEditor.ShaderGraph.AbstractMaterialNode", doomed), Sg.Array("UnityEditor.Graphing.IEdge", new object[0]),
                        Sg.Array("UnityEditor.ShaderGraph.GroupData", new object[0]), Sg.Array("UnityEditor.ShaderGraph.StickyNoteData", new object[0]), null);
                    break;
                case "Add Property":
                    shownInput = AddInput(window, graph, AssetViews.Value(values, "type"), AssetViews.Value(values, "name", false));
                    result = "Property added: " + InputLabel(shownInput);
                    foreach (var entry in values.Where(item => !AssetViews.KeyIs(item.path, "type", "name")))
                        changes.Add(SetInputField(window, graph, shownInput, entry.path, AssetViews.Text(entry.value)));
                    break;
                case "Remove Property":
                    var input = FindInput(graph, AssetViews.Value(values, "name"), true);
                    touched.AddRange(model.nodes.Where(item => Sg.Has(item, "property") && Sg.Get(item, "property") == input).SelectMany(model.Neighbours));
                    result = "Property removed: " + InputLabel(input);
                    Undo(graph, "Delete Graph Input(s)");
                    var delete = Activator.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.Drawing.DeleteShaderInputAction"), true);
                    Sg.Call(Sg.Get(delete, "shaderInputsToDelete"), "Add", input);
                    Sg.Call(Sg.Get(ShaderGraphWindow.GraphObject(window), "graphDataStore"), "Dispatch", delete);
                    break;
                case "Add Block":
                    var block = AddBlock(graph, AssetViews.Value(values, "name"));
                    touched.Add(block);
                    result = "Block added: " + Sg.Get(Sg.Get(block, "descriptor"), "displayName");
                    break;
                case "Convert To Nodes":
                    result = ConvertToNodes(window, graph, model.Find(AssetViews.Value(values, "node")), touched);
                    break;
                case "Add Sticky Note":
                    var near = AssetViews.Value(values, "near", false);
                    var anchor = near == null ? model.nodes.Where(item => !model.IsBlock(item)).Select(NodeRect).DefaultIfEmpty(new Rect(0f, 0f, 200f, 140f)).OrderBy(rect => rect.y).First() : NodeRect(model.Find(near));
                    var note = Activator.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.StickyNoteData"), AssetViews.Value(values, "title"),
                        AssetViews.Value(values, "content", false) ?? string.Empty, new Rect(anchor.x, anchor.y - 170f, 220f, 140f));
                    Undo(graph, "Create Sticky Note");
                    Sg.Call(graph, "AddStickyNote", note);
                    touched.Add(note);
                    result = "Sticky Note added: " + Sg.Get(note, "title");
                    break;
                case "Remove Sticky Note":
                    var title = AssetViews.Value(values, "title");
                    var notes = Sg.Items(Sg.Get(graph, "stickyNotes")).Where(item => AssetViews.KeyIs(title, (string)Sg.Get(item, "title"))).ToList();
                    if (notes.Count == 0)
                        throw new ArgumentException("Sticky Note was not found: " + title);
                    Undo(graph, "Delete Sticky Note");
                    foreach (var item in notes)
                        Sg.Call(graph, "RemoveStickyNote", item);
                    result = "Sticky Note removed: " + title;
                    break;
                default:
                    // The group's title, as typed after Group Selection; "title" like a Sticky Note's, or "name".
                    var unknown = values.FirstOrDefault(item => !AssetViews.KeyIs(item.path, "nodes", "name", "title"));
                    if (unknown != null)
                        throw new ArgumentException("Group Selection takes nodes and title: " + unknown.path);
                    var members = AssetViews.Value(values, "nodes").Split(',').Select(item => model.Find(item.Trim())).ToList();
                    var first = ((Rect)Sg.Get(Sg.Get(members[0], "drawState"), "position")).position;
                    framedGroup = Activator.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.GroupData"), AssetViews.Value(values, "title", false) ?? AssetViews.Value(values, "name", false) ?? "Group", first);
                    Undo(graph, "Create Group Node");
                    Sg.Call(graph, "CreateGroup", framedGroup);
                    foreach (var member in members)
                        Sg.Call(graph, "SetGroup", member, framedGroup);
                    touched.AddRange(members);
                    result = "Group added: " + Sg.Get(framedGroup, "title") + " (" + string.Join(", ", members.Select(model.Id)) + ")";
                    break;
            }
            Finish(window, path, structure, touched, shownInput, changes, framedGroup);
            var final = new Model(ShaderGraphWindow.Graph(window));
            if (added != null)
            {
                result += ": " + NodeLine(final, added);
                touched.Add(added);
            }
            changes.AddRange(Truncations(final, touched));
            return result + (changes.Count == 0 ? string.Empty : "; " + string.Join("; ", changes));
        }

        private static object AddNode(EditorWindow window, Model model, PropertyValue[] values, List<object> touched)
        {
            var graph = model.graph;
            // Wiring keys are lowercase; the node's own inputs and settings keep their Title Case labels (X, Y, From).
            Func<string, string> wiring = key => values.Where(item => item.path == key).Select(item => AssetViews.Text(item.value)).FirstOrDefault();
            var entry = FindNodeEntry(window, wiring("node") ?? throw new ArgumentException("Action requires --set node=<value>."));
            var provider = Sg.Get(ShaderGraphWindow.EditorView(window), "m_SearchWindowProvider");
            var node = Sg.Call(provider, "CopyNodeForGraph", Sg.Get(entry, "node"));
            var connect = wiring("connect");
            var from = wiring("from");
            var x = wiring("x");
            var y = wiring("y");
            Vector2 position;
            if (x != null && y != null)
                position = new Vector2(Numbers(x, 1)[0], Numbers(y, 1)[0]);
            else if (connect != null)
                position = NodeRect(SlotOf(model, connect, true).Key).position - new Vector2(260f, 0f);
            else if (from != null)
                position = NodeRect(SlotOf(model, from, false).Key).position + new Vector2(260f, 0f);
            else
            {
                var graphView = ShaderGraphWindow.GraphView(window);
                position = graphView.contentViewContainer.WorldToLocal(graphView.worldBound.center) - new Vector2(100f, 60f);
            }
            PlaceAndAdd(model, node, position);
            touched.Add(node);
            // The rest of --set fills the new node before it is wired (a Custom Function gets its slots first): B=2, Outputs="Out: Vector3".
            foreach (var value in values.Where(item => !new[] { "node", "x", "y", "connect", "from", "group" }.Contains(item.path)))
                SetNodeField(window, new Model(graph), new Model(graph).Id(node) + "." + value.path, AssetViews.Text(value.value), touched);
            var fresh = new Model(graph);
            if (connect != null)
            {
                var target = SlotOf(fresh, connect, true);
                var output = fresh.Outputs(node).FirstOrDefault(slot => (bool)Sg.Call(slot, "IsCompatibleWith", target.Value));
                if (output == null)
                    throw new InvalidOperationException("The new node has no output that fits " + connect);
                Connect(graph, node, output, target.Key, target.Value);
                touched.Add(target.Key);
            }
            if (from != null)
            {
                var source = SlotOf(fresh, from, false);
                var input = fresh.Inputs(node).FirstOrDefault(slot => (bool)Sg.Call(source.Value, "IsCompatibleWith", slot));
                if (input == null)
                    throw new InvalidOperationException("The new node has no input that fits " + from);
                Connect(graph, source.Key, source.Value, node, input);
                touched.Add(source.Key);
            }
            var groupName = wiring("group");
            if (groupName != null)
            {
                var group = Sg.Items(Sg.Get(graph, "groups")).FirstOrDefault(item => AssetViews.KeyIs(groupName, (string)Sg.Get(item, "title")));
                if (group == null)
                    throw new ArgumentException("Group was not found: " + groupName);
                Sg.Call(graph, "SetGroup", node, group);
            }
            return node;
        }

        private static KeyValuePair<object, object> SlotOf(Model model, string key, bool input)
        {
            // A block or any node with a single slot on that side is addressed by the node alone: connect="Base Color".
            var whole = model.TryFind(key);
            if (whole != null)
            {
                var only = (input ? model.Inputs(whole) : model.Outputs(whole)).ToList();
                if (only.Count == 1)
                    return new KeyValuePair<object, object>(whole, only[0]);
            }
            var dot = key.LastIndexOf('.');
            if (dot <= 0)
                throw new ArgumentException("Use <node>.<slot>: " + key);
            var node = model.Find(key.Substring(0, dot));
            var slots = input ? model.Inputs(node) : model.Outputs(node);
            var slot = slots.FirstOrDefault(item => AssetViews.KeyIs(key.Substring(dot + 1), SlotName(item), Sg.Get(item, "id").ToString()));
            if (slot == null)
            {
                var other = (input ? model.Outputs(node) : model.Inputs(node)).Any(item => AssetViews.KeyIs(key.Substring(dot + 1), SlotName(item)));
                throw new ArgumentException((other ? key.Substring(dot + 1) + " is an " + (input ? "output" : "input") + " of " + model.Label(node) + "; " : model.Label(node) + " ") +
                    (input ? "inputs: " : "outputs: ") + string.Join(", ", slots.Select(SlotName)));
            }
            return new KeyValuePair<object, object>(node, slot);
        }

        // Entries of the Create Node window: "Math/Basic/Multiply", sub graphs and "Properties/Property: Tint".
        private static IEnumerable<object> NodeEntries(EditorWindow window)
        {
            var provider = Sg.Get(ShaderGraphWindow.EditorView(window), "m_SearchWindowProvider");
            Sg.Set(provider, "connectedPort", null);
            Sg.Set(provider, "target", null);
            Sg.Call(provider, "GenerateNodeEntries");
            return Sg.Items(Sg.Get(provider, "currentNodeEntries"));
        }

        private static string EntryTitle(object entry)
        {
            return string.Join("/", (string[])Sg.Get(entry, "title"));
        }

        private static object FindNodeEntry(EditorWindow window, string key)
        {
            var entries = NodeEntries(window).ToList();
            var matches = entries.Where(entry => AssetViews.KeyIs(key, EntryTitle(entry))).ToList();
            if (matches.Count == 0)
                matches = entries.Where(entry => AssetViews.KeyIs(key, ((string[])Sg.Get(entry, "title")).Last())).ToList();
            if (matches.Count != 1)
                throw new ArgumentException((matches.Count == 0 ? "Node was not found: " : "Node name is ambiguous: ") + key +
                    ". Search with: creation-templates --path <graph> --query \"...\"" + (matches.Count > 1 ? " Matches: " + string.Join(", ", matches.Select(EntryTitle)) : string.Empty));
            return matches[0];
        }

        internal override CreationTemplateData[] Templates(string path, UnityEngine.Object asset)
        {
            var window = ShaderGraphWindow.Open(path);
            return NodeEntries(window).Select(entry =>
            {
                var synonyms = Sg.Get(Sg.Get(entry, "node"), "synonyms") as string[];
                return new CreationTemplateData { name = EntryTitle(entry), keywords = synonyms == null ? null : string.Join(", ", synonyms) };
            }).ToArray();
        }

        // The Blackboard "+" menu: property types by their menu names, keywords as "Boolean Keyword" / "Enum Keyword".
        private static object AddInput(EditorWindow window, object graph, string type, string name)
        {
            var viewModel = Sg.Get(Sg.Get(ShaderGraphWindow.EditorView(window), "blackboardController"), "ViewModel");
            var menus = new List<KeyValuePair<string, object>>();
            foreach (var pair in Sg.Items(Sg.Get(viewModel, "propertyNameToAddActionMap")))
                menus.Add(new KeyValuePair<string, object>((string)Sg.Get(pair, "Key"), Sg.Get(pair, "Value")));
            foreach (var pair in Sg.Items(Sg.Get(viewModel, "defaultKeywordNameToAddActionMap")))
                menus.Add(new KeyValuePair<string, object>((string)Sg.Get(pair, "Key") + " Keyword", Sg.Get(pair, "Value")));
            foreach (var pair in Sg.Items(Sg.Get(viewModel, "builtInKeywordNameToAddActionMap")))
                menus.Add(new KeyValuePair<string, object>((string)Sg.Get(pair, "Key"), Sg.Get(pair, "Value")));
            var match = menus.FirstOrDefault(item => AssetViews.KeyIs(type, item.Key));
            if (match.Value == null)
                throw new ArgumentException("Property type was not found: " + type + ". Types: " + string.Join(", ", menus.Select(item => item.Key)));
            var before = new HashSet<object>(Inputs(graph));
            // From the "+" menu the new field opens for renaming; a name given here must not be left in an open text field.
            var source = Sg.Get(match.Value, "addInputActionType");
            Sg.Set(match.Value, "addInputActionType", System.Enum.ToObject(source.GetType(), 0));
            try
            {
                Sg.Call(Sg.Get(ShaderGraphWindow.GraphObject(window), "graphDataStore"), "Dispatch", match.Value);
            }
            finally
            {
                Sg.Set(match.Value, "addInputActionType", source);
            }
            var input = Inputs(graph).FirstOrDefault(item => !before.Contains(item));
            if (input == null)
                throw new InvalidOperationException("Shader Graph did not add the property.");
            if (!string.IsNullOrEmpty(name))
                Dispatch(Sg.Get(ShaderGraphWindow.GraphObject(window), "graphDataStore"), "UnityEditor.ShaderGraph.Drawing.ChangeDisplayNameAction",
                    ("shaderInputReference", input), ("newDisplayNameValue", name));
            return input;
        }

        // Blocks of the active targets that are not in the stack yet, by name ("Emission", "Alpha Clip Threshold").
        private static object AddBlock(object graph, string name)
        {
            var descriptors = Sg.Items(Sg.Get(graph, "blockFieldDescriptors")).Where(item => !(bool)Sg.Get(item, "isHidden")).ToList();
            var descriptor = descriptors.FirstOrDefault(item => AssetViews.KeyIs(name, (string)Sg.Get(item, "displayName"), (string)Sg.Get(item, "name")));
            if (descriptor == null)
                throw new ArgumentException("Block was not found: " + name + ". Blocks: " + string.Join(", ", descriptors.Select(item => (string)Sg.Get(item, "displayName")).Distinct()));
            var vertex = Sg.Get(descriptor, "shaderStage").ToString() == "Vertex";
            var context = Sg.Get(graph, vertex ? "vertexContext" : "fragmentContext");
            if (Sg.Items(Sg.Get(context, "blocks")).Any(item => (string)Sg.Get(Sg.Get(item, "value"), "name") == (string)Sg.Get(descriptor, "name") ||
                    Sg.Get(Sg.Get(item, "value"), "descriptor") == descriptor))
                throw new InvalidOperationException("The stack already has " + name + ".");
            var block = Activator.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.BlockNode"));
            Sg.Call(block, "Init", descriptor);
            Sg.Set(block, "owner", graph);
            Undo(graph, "Add " + Sg.Get(descriptor, "displayName"));
            Sg.Call(graph, "AddBlock", block, context, Sg.Items(Sg.Get(context, "blocks")).Count());
            return block;
        }

        // ---------- preview (MCP) ----------

        // Node previews in order; with chain the path between the first and the last node, output included as "Output".
        // A scene object shows its graph with the values its renderer draws with now (Play Mode textures included).
        internal static string Preview(BridgeRequest request)
        {
            var live = request.path.StartsWith("/", StringComparison.Ordinal) ? MaterialPreviewService.LiveMaterial(MaterialPreviewService.SceneRenderer(request.path)) : null;
            try
            {
                var graphPath = live == null ? request.path : AssetDatabase.GetAssetPath(live.shader);
                if (live != null && !graphPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(request.path + " draws with " + live.shader.name + ", which is not a Shader Graph; use shapes.");
                var window = ShaderGraphWindow.Open(graphPath);
                var graph = ShaderGraphWindow.Graph(window);
                var restore = live == null ? new List<Action>() : ShowLiveValues(graph, live);
                try
                {
                    return PreviewNodes(request, window, graph, graphPath);
                }
                finally
                {
                    if (restore.Count > 0)
                    {
                        restore.ForEach(action => action());
                        DirtyPropertyNodes(graph);
                        ShaderGraphWindow.Sync(window);
                    }
                }
            }
            finally
            {
                if (live != null)
                    UnityEngine.Object.DestroyImmediate(live);
            }
        }

        private static string PreviewNodes(BridgeRequest request, EditorWindow window, object graph, string graphPath)
        {
            var model = new Model(graph);
            var keys = (request.names ?? new string[0]).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            if (keys.Count == 0)
                throw new ArgumentException("nodes is required: node ids or names, \"Output\" for the master preview.");
            var nodes = keys.Select(key => AssetViews.KeyIs(key, "Output", "Master") ? null : model.Find(key)).ToList();
            if (request.boolValue && nodes.Count == 2)
                nodes = model.Chain(nodes[0], nodes[1]);
            if (nodes.Count > 16)
                throw new ArgumentException("A preview grid holds at most 16 nodes; this chain has " + nodes.Count + ".");
            var expanded = false;
            foreach (var node in nodes.Where(item => item != null && (bool)Sg.Get(item, "hasPreview") && !(bool)Sg.Get(item, "previewExpanded")))
            {
                // Collapsed previews are not rendered; expand them as the developer would to look.
                Sg.Set(node, "previewExpanded", true);
                expanded = true;
            }
            if (expanded)
                ShaderGraphWindow.Sync(window);
            if (nodes.Contains(null))
                ShaderGraphWindow.ShowMainPreview(window);
            ShaderGraphWindow.WaitForPreviews(window, nodes.Where(item => item != null && (bool)Sg.Get(item, "hasPreview")), 60);
            var screenshots = new List<string>();
            var labels = new List<string>();
            var errors = new List<string>();
            for (var index = 0; index < nodes.Count; index++)
            {
                // The Main Preview drawn outside the window's own update can pick up stale textures, so the output is
                // the graph's material rendered as the Inspector does, on the Main Preview's default mesh.
                var file = nodes[index] != null ? ShaderGraphWindow.CapturePreview(window, nodes[index], index) :
                    MaterialPreviewService.Render(request.path, new[] { SpritePreview(graph) ? "Quad" : "Sphere" }, 256, "shadergraph-output-")[0];
                if (file == null)
                {
                    errors.Add(model.Label(nodes[index]) + ": no preview");
                    continue;
                }
                screenshots.Add(file);
                labels.Add((index + 1) + ". " + (nodes[index] == null ? "Output" : model.Label(nodes[index])));
            }
            if (expanded)
                Arrange(window, graph);
            // The camera shows every previewed node, and the master stack for the output.
            var shown = nodes.Where(item => item != null).ToList();
            if (nodes.Contains(null))
                shown.AddRange(new[] { Sg.Get(graph, "vertexContext"), Sg.Get(graph, "fragmentContext") });
            ShaderGraphWindow.Frame(window, shown);
            ShaderGraphWindow.SaveAfterLayout(window);
            if (screenshots.Count == 0)
                throw new InvalidOperationException("These nodes have no preview (property or output-only nodes show none).");
            errors.AddRange(Errors(model, graphPath, window));
            return new JsonText().Add("screenshots", screenshots).Add("labels", labels).AddIf(errors.Count > 0, "errors", errors).ToString();
        }

        // Blackboard values for the previews only, as if typed into the properties; the returned actions put the graph's own back.
        private static List<Action> ShowLiveValues(object graph, Material live)
        {
            var restore = new List<Action>();
            foreach (var input in Sg.Items(Sg.Get(graph, "properties")))
            {
                var reference = (string)Sg.Get(input, "referenceName");
                if (!live.HasProperty(reference) || !Sg.Has(input, "value"))
                    continue;
                var current = Sg.Get(input, "value");
                if (current != null && Sg.Has(current, "texture"))
                {
                    var texture = Sg.Get(current, "texture");
                    Sg.Set(current, "texture", live.GetTexture(reference));
                    restore.Add(() => Sg.Set(current, "texture", texture));
                    continue;
                }
                var next = current is float ? (object)live.GetFloat(reference) : current is Color ? (object)live.GetColor(reference) :
                    current is Vector4 ? (object)live.GetVector(reference) : current is bool ? (object)(live.GetFloat(reference) > 0.5f) : null;
                if (next == null || Equals(next, current))
                    continue;
                Sg.Set(input, "value", next);
                restore.Add(() => Sg.Set(input, "value", current));
            }
            if (restore.Count > 0)
                DirtyPropertyNodes(graph);
            return restore;
        }

        private static void DirtyPropertyNodes(object graph)
        {
            foreach (var node in new Model(graph).nodes.Where(item => Sg.Has(item, "property") && Sg.Get(item, "property") != null))
                Sg.Call(node, "Dirty", Sg.Enum("UnityEditor.Graphing.ModificationScope", "Node"));
        }

        private static bool SpritePreview(object graph)
        {
            var target = Sg.Items(Sg.Get(graph, "activeTargets")).LastOrDefault(item => (bool)Sg.Call(item, "IsActive"));
            return target != null && (bool)Sg.Get(target, "prefersSpritePreview");
        }

        // ---------- shared ----------

        private static void Finish(EditorWindow window, string path, string structure, List<object> touched, object shownInput, List<string> changes, object group = null)
        {
            var graph = ShaderGraphWindow.Graph(window);
            Sg.Call(graph, "ValidateGraph");
            ShaderGraphWindow.Sync(window);
            if (Structure(graph) != structure)
                Arrange(window, graph);
            var settled = ShaderGraphWindow.WaitForPreviews(window, 8);
            ShaderGraphWindow.Save(window);
            var model = new Model(graph);
            var errors = Errors(model, path, window);
            if (errors.Count > 0)
                changes.Add((settled ? "Errors: " : "Errors (previews still compiling, may be outdated; recheck with asset-info): ") + string.Join(" | ", errors));
            if (shownInput != null && Inputs(graph).Contains(shownInput))
                ShaderGraphWindow.ShowInBlackboard(window, shownInput);
            else
                ShaderGraphWindow.Frame(window, touched.Where(item => model.nodes.Contains(item) || item.GetType().Name == "StickyNoteData").Distinct(), group);
            ShaderGraphWindow.SaveAfterLayout(window);
        }

        private static string Structure(object graph)
        {
            var model = new Model(graph);
            // Group membership counts too: a group is laid out as one band.
            return string.Join(",", model.nodes.Select(node => (string)Sg.Get(node, "objectId") + (Sg.Get(node, "group") == null ? string.Empty : "@" + Sg.Get(Sg.Get(node, "group"), "objectId")))
                .OrderBy(id => id, StringComparer.Ordinal)) + "|" + string.Join(",", model.EdgeKeys()) +
                "|" + string.Join(",", Sg.Items(Sg.Get(graph, "stickyNotes")).Select(note => (string)Sg.Get(note, "objectId")).OrderBy(id => id, StringComparer.Ordinal));
        }

        // Nodes and wires changed: the graph is laid out again so it always reads left to right into the stack.
        // Columns: a node stands one column left of the furthest node it feeds. Rows: its output lines up with the inputs it feeds.
        private static void Arrange(EditorWindow window, object graph)
        {
            const float gapX = 80f;
            const float gapY = 30f;
            var model = new Model(graph);
            var graphView = ShaderGraphWindow.GraphView(window);
            // Expanded previews and new ports change node sizes only on the panel's next layout pass.
            if (graphView.panel != null)
                Sg.Call(graphView.panel, "ValidateLayout");
            var views = new Dictionary<object, VisualElement>();
            foreach (var view in graphView.nodes.ToList())
                if (Sg.Has(view, "node") && Sg.Get(view, "node") != null)
                    views[Sg.Get(view, "node")] = view;
            Func<object, bool> anchored = node => model.IsBlock(node) || node.GetType().Name == "SubGraphOutputNode";
            var movable = model.nodes.Where(node => !anchored(node)).ToList();
            if (movable.Count == 0)
                return;

            var sizes = movable.ToDictionary(node => node, node => Size(model, node, views));
            // A sticky note sits above the node it was written by (the one closest to its lower edge) and takes room in that column.
            var notes = new Dictionary<object, List<object>>();
            foreach (var note in Sg.Items(Sg.Get(graph, "stickyNotes")))
            {
                var noteRect = (Rect)Sg.Get(note, "position");
                var owner = movable.OrderBy(node => Vector2.Distance(new Vector2(NodeRect(node).center.x, NodeRect(node).yMin), new Vector2(noteRect.center.x, noteRect.yMax))).First();
                if (!notes.ContainsKey(owner))
                    notes[owner] = new List<object>();
                notes[owner].Add(note);
            }
            var noteSpace = movable.ToDictionary(node => node, node => notes.ContainsKey(node) ? notes[node].Sum(note => ((Rect)Sg.Get(note, "position")).height + 20f) : 0f);
            foreach (var pair in notes)
                sizes[pair.Key] = new Vector2(Mathf.Max(sizes[pair.Key].x, pair.Value.Max(note => ((Rect)Sg.Get(note, "position")).width)), sizes[pair.Key].y + noteSpace[pair.Key]);
            var depth = new Dictionary<object, int>();
            Func<object, int> depthOf = null;
            depthOf = node =>
            {
                int value;
                if (depth.TryGetValue(node, out value))
                    return value;
                var targets = model.Targets(node).Select(item => item.Key).Distinct().ToList();
                value = 1 + (targets.Count == 0 ? 0 : targets.Max(target => anchored(target) ? 0 : depthOf(target)));
                depth[node] = value;
                return value;
            };
            foreach (var node in movable)
                depthOf(node);

            // Anchors stay where the developer put the stack (or the sub graph output).
            var tops = new Dictionary<object, float>();
            var anchorRects = model.nodes.Where(anchored).Select(node => new KeyValuePair<object, Rect>(node, AnchorRect(graphView, node, views))).ToList();
            var right = anchorRects.Count == 0 ? 0f : anchorRects.Min(item => item.Value.xMin);
            var top = anchorRects.Count == 0 ? 0f : anchorRects.Min(item => item.Value.yMin);
            Func<object, int, float> inputY = (target, slotIndex) =>
            {
                if (anchored(target))
                {
                    var rect = anchorRects.First(item => item.Key == target).Value;
                    return model.IsBlock(target) ? rect.center.y : rect.yMin + 40f + slotIndex * 22f;
                }
                return tops[target] + 40f + slotIndex * 22f;
            };

            var placed = new Dictionary<object, Rect>();
            for (var column = 1; column <= depth.Values.Max(); column++)
            {
                var nodes = movable.Where(node => depth[node] == column).ToList();
                var width = nodes.Max(node => sizes[node].x);
                var wanted = nodes.ToDictionary(node => node, node =>
                {
                    var links = model.Targets(node).ToList();
                    return links.Count == 0 ? float.MaxValue : links.Average(link => inputY(link.Key, link.Value) - 40f);
                });
                var bottom = float.MinValue;
                foreach (var node in nodes.OrderBy(node => wanted[node]))
                {
                    var y = Mathf.Max(wanted[node] == float.MaxValue ? top : wanted[node] - noteSpace[node], bottom + gapY);
                    tops[node] = y + noteSpace[node];
                    bottom = y + sizes[node].y;
                    placed[node] = new Rect(right - gapX - sizes[node].x, y, sizes[node].x, sizes[node].y);
                }
                right -= gapX + width;
            }
            KeepOutOfGroups(model, placed, depth, gapY);
            foreach (var pair in notes)
            {
                var y = placed[pair.Key].y;
                foreach (var note in pair.Value)
                {
                    var noteRect = (Rect)Sg.Get(note, "position");
                    var moved = new Rect(placed[pair.Key].x, y, noteRect.width, noteRect.height);
                    y += noteRect.height + 20f;
                    Sg.Set(note, "position", moved);
                    foreach (var element in graphView.graphElements.ToList().Where(element => element.userData == note))
                        element.SetPosition(moved);
                }
            }
            foreach (var pair in placed)
            {
                var drawState = Sg.Get(pair.Key, "drawState");
                var rect = (Rect)Sg.Get(drawState, "position");
                var nodeTop = pair.Value.y + noteSpace[pair.Key];
                Sg.Set(drawState, "position", new Rect(pair.Value.x, nodeTop, rect.width, rect.height));
                Sg.Set(pair.Key, "drawState", drawState);
                VisualElement view;
                if (views.TryGetValue(pair.Key, out view))
                    ((UnityEditor.Experimental.GraphView.GraphElement)view).SetPosition(new Rect(pair.Value.x, nodeTop, 0f, 0f));
            }
        }

        // Groups are laid out as bands: a group frame spans its members' columns and holds only them, so other nodes of
        // those columns go above or below the frame. Units (a group or a single node) keep their order and only move down.
        private static void KeepOutOfGroups(Model model, Dictionary<object, Rect> placed, Dictionary<object, int> depth, float gapY)
        {
            const float title = 60f;
            const float padding = 25f;
            var units = placed.Keys.GroupBy(node => Sg.Get(node, "group") ?? node).Select(unit => unit.ToList())
                .OrderBy(unit => unit.Average(node => placed[node].center.y)).ToList();
            var bottoms = new Dictionary<int, float>();
            foreach (var unit in units)
            {
                var grouped = Sg.Get(unit[0], "group") != null;
                var columns = Enumerable.Range(unit.Min(node => depth[node]), unit.Max(node => depth[node]) - unit.Min(node => depth[node]) + 1).ToList();
                var top = unit.Min(node => placed[node].y) - (grouped ? title : 0f);
                var floor = columns.Select(column => bottoms.TryGetValue(column, out var bottom) ? bottom + gapY : float.MinValue).Max();
                var shift = Mathf.Max(0f, floor - top);
                foreach (var node in unit)
                    placed[node] = new Rect(placed[node].x, placed[node].y + shift, placed[node].width, placed[node].height);
                var unitBottom = unit.Max(node => placed[node].yMax) + (grouped ? padding : 0f);
                foreach (var column in columns)
                    bottoms[column] = bottoms.TryGetValue(column, out var bottom) ? Mathf.Max(bottom, unitBottom) : unitBottom;
            }
        }

        private static Rect AnchorRect(UnityEditor.Experimental.GraphView.GraphView graphView, object node, Dictionary<object, VisualElement> views)
        {
            VisualElement view;
            if (views.TryGetValue(node, out view) && !float.IsNaN(view.worldBound.width) && view.worldBound.width > 0f)
                return graphView.contentViewContainer.WorldToLocal(view.worldBound);
            var rect = (Rect)Sg.Get(Sg.Get(node, "drawState"), "position");
            return new Rect(rect.position, new Vector2(200f, 30f));
        }

        // Laid-out size when the view has one; a new node is measured by its rows until the window lays it out.
        private static Vector2 Size(Model model, object node, Dictionary<object, VisualElement> views)
        {
            VisualElement view;
            if (views.TryGetValue(node, out view) && !float.IsNaN(view.layout.width) && view.layout.width > 0f)
                return view.layout.size;
            var rows = Mathf.Max(model.Inputs(node).Count, model.Outputs(node).Count) + Controls(node).Count();
            var preview = (bool)Sg.Get(node, "hasPreview") && (bool)Sg.Get(node, "previewExpanded") ? 140f : 0f;
            return new Vector2(model.Inputs(node).Count > 0 ? 260f : 160f, 50f + rows * 22f + preview);
        }

        private static readonly object CompileErrorBadges = new object();

        // Node badges (validation and preview compile errors) and the compiled shader's own errors.
        private static List<string> Errors(Model model, string path, EditorWindow window)
        {
            var result = new List<string>();
            var unplaced = CompileErrors(window, model);
            var messages = Sg.Get(model.graph, "messageManager");
            if (messages != null)
                foreach (var pair in Sg.Items(Sg.Call(messages, "GetNodeMessages")))
                {
                    var node = model.nodes.FirstOrDefault(item => (string)Sg.Get(item, "objectId") == (string)Sg.Get(pair, "Key"));
                    foreach (var message in Sg.Items(Sg.Get(pair, "Value")))
                    {
                        var warning = Sg.Get(message, "severity").ToString() != "Error";
                        result.Add((warning ? "warning: " : string.Empty) + (node == null ? "Graph" : model.Label(node)) + ": " + FirstLine((string)Sg.Get(message, "message")));
                    }
                }
            result.AddRange(unplaced);
            var shader = unplaced.Count > 0 || result.Count > 0 ? null : AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader != null)
                foreach (var message in ShaderUtil.GetShaderMessages(shader).Where(item => item.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).Take(5))
                    result.Add("shader: " + FirstLine(message.message) + (message.line > 0 ? " (line " + message.line + ")" : string.Empty));
            // A Custom Function's .hlsl errors come from its own preview, which the graph does not badge.
            var previews = Sg.Get(Sg.Get(ShaderGraphWindow.EditorView(window), "previewManager"), "m_RenderDatas") as System.Collections.IDictionary;
            if (previews != null)
                foreach (System.Collections.DictionaryEntry preview in previews)
                {
                    var shaderData = Sg.Get(preview.Value, "shaderData");
                    var previewShader = shaderData == null ? null : Sg.Get(shaderData, "shader") as Shader;
                    if (SourceFile(preview.Key) == null || previewShader == null || !ShaderUtil.ShaderHasError(previewShader))
                        continue;
                    foreach (var message in ShaderUtil.GetShaderMessages(previewShader).Where(item => item.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).Take(2))
                        result.Add(model.Label(preview.Key) + ": " + FirstLine(message.message) + (string.IsNullOrEmpty(message.file) ? string.Empty : " at " + message.file + "(" + message.line + ")"));
                }
            var changed = model.nodes.Select(SourceChanged).Where(file => file != null).Distinct();
            result.InsertRange(0, changed.Select(file => "warning: " + file + " changed on disk after Unity imported it; run compile"));
            return result.Distinct().Take(20).ToList();
        }

        // The .hlsl file of a Custom Function when it was edited outside Unity and not imported yet.
        private static string SourceChanged(object node)
        {
            var file = SourceFile(node);
            return file != null && SourceFreshness.Changed(file) ? file : null;
        }

        private static string SourceFile(object node)
        {
            if (node == null || node.GetType().Name != "CustomFunctionNode" || Sg.Get(node, "sourceType").ToString() != "File")
                return null;
            var file = AssetDatabase.GUIDToAssetPath((string)Sg.Get(node, "functionSource"));
            return string.IsNullOrEmpty(file) ? null : file;
        }

        // Shader Graph drops the compile errors of the main preview (only expanded node previews report theirs), so an error in a
        // collapsed Custom Function shows only in the Console. The error line is traced to the node whose variable or function it is in,
        // and put on that node as a badge, where the developer would look. Errors that belong to no node are returned.
        private static List<string> CompileErrors(EditorWindow window, Model model)
        {
            var result = new List<string>();
            var messages = Sg.Get(model.graph, "messageManager");
            Sg.Call(messages, "ClearAllFromProvider", CompileErrorBadges);
            var data = Sg.Get(Sg.Get(ShaderGraphWindow.EditorView(window), "previewManager"), "masterRenderData");
            var shaderData = data == null ? null : Sg.Get(data, "shaderData");
            var shader = shaderData == null ? null : Sg.Get(shaderData, "shader") as Shader;
            if (shader == null || !ShaderUtil.ShaderHasError(shader))
                return result;
            var lines = ((string)Sg.Get(shaderData, "shaderString") ?? string.Empty).Split('\n');
            foreach (var message in ShaderUtil.GetShaderMessages(shader).Where(item => item.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).Take(5))
            {
                var node = message.line > 0 && message.line <= lines.Length ? NodeAtLine(model, lines, message.line - 1) : null;
                if (node == null)
                    result.Add("shader: " + FirstLine(message.message));
                else
                    Sg.Call(messages, "AddOrAppendError", CompileErrorBadges, (string)Sg.Get(node, "objectId"), message);
            }
            ShaderGraphWindow.Sync(window);
            return result;
        }

        private static object NodeAtLine(Model model, string[] lines, int index)
        {
            var byVariable = model.nodes.FirstOrDefault(node => lines[index].Contains((string)Sg.Get(node, "objectId")));
            if (byVariable != null)
                return byVariable;
            for (var line = index; line >= 0; line--)
            {
                var function = System.Text.RegularExpressions.Regex.Match(lines[line], @"^\s*void\s+(\w+)\s*\(");
                if (function.Success)
                    return model.nodes.FirstOrDefault(node => node.GetType().Name == "CustomFunctionNode" &&
                        function.Groups[1].Value.StartsWith((string)Sg.Get(node, "functionName") + "_", StringComparison.Ordinal));
            }
            return null;
        }

        private static string FirstLine(string text)
        {
            return (text ?? string.Empty).Split('\n')[0].Trim();
        }

        private static void Undo(object graph, string name)
        {
            Sg.Call(Sg.Get(graph, "owner"), "RegisterCompleteObjectUndo", name);
        }

        private static void Dispatch(object store, string actionType, params (string name, object value)[] fields)
        {
            var action = Activator.CreateInstance(Sg.Type(actionType), true);
            foreach (var field in fields)
                Sg.Set(action, field.name, field.value);
            Sg.Call(store, "Dispatch", action);
        }

        private static void Connect(object graph, object fromNode, object fromSlot, object toNode, object toSlot)
        {
            Undo(graph, "Connect Edge");
            Sg.Call(graph, "Connect", Sg.Call(fromNode, "GetSlotReference", Sg.Get(fromSlot, "id")), Sg.Call(toNode, "GetSlotReference", Sg.Get(toSlot, "id")));
        }

        private static void Disconnect(object graph, IEnumerable<object> edges)
        {
            var list = edges.ToList();
            if (list.Count == 0)
                return;
            Undo(graph, "Delete Edge");
            foreach (var edge in list)
                Sg.Call(graph, "RemoveEdge", edge);
        }

        private static void MoveNode(object graph, object node, Vector2 position)
        {
            Undo(graph, "Move Elements");
            var drawState = Sg.Get(node, "drawState");
            var rect = (Rect)Sg.Get(drawState, "position");
            Sg.Set(drawState, "position", new Rect(position, rect.size));
            Sg.Set(node, "drawState", drawState);
        }

        private static Rect NodeRect(object node)
        {
            var rect = (Rect)Sg.Get(Sg.Get(node, "drawState"), "position");
            return new Rect(rect.position, new Vector2(Mathf.Max(rect.width, 200f), Mathf.Max(rect.height, 140f)));
        }

        private static void SetSlotValue(object graph, object node, object slot, string raw)
        {
            Undo(graph, "Change " + Sg.Get(node, "name"));
            var property = new[] { "channel", "space", "screenSpaceType", "value", "defaultType" }.Select(name => FindProperty(slot, name)).FirstOrDefault(item => item != null);
            if (Sg.Has(slot, "texture"))
                Sg.Set(slot, "texture", AssetViews.KeyIs(raw, "None") ? null : LoadAsset<Texture>(raw));
            else if (property != null && property.PropertyType == typeof(Matrix4x4) && VectorSize(slot) > 0)
            {
                // Dynamic inputs (Multiply.B) keep a vector in the first matrix row, as their port field edits it.
                var matrix = (Matrix4x4)property.GetValue(slot, null);
                var numbers = Numbers(raw, 4);
                matrix.SetRow(0, new Vector4(numbers[0], numbers[1], numbers[2], numbers[3]));
                property.SetValue(slot, matrix, null);
            }
            else if (property != null)
                property.SetValue(slot, Parse(property.PropertyType, raw), null);
            else
                throw new NotSupportedException(SlotName(slot) + " takes no typed value; connect a node to it.");
            Sg.Call(node, "Dirty", Sg.Enum("UnityEditor.Graphing.ModificationScope", "Node"));
        }

        private static PropertyInfo FindProperty(object target, string name)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetSetMethod(true) != null)
                    return property;
            }
            return null;
        }

        private struct Control
        {
            internal string label;
            internal PropertyInfo property;
            // A dropdown inside a compound setting, such as Transform's From and To.
            internal FieldInfo field;
        }

        // Settings drawn on the node body ([EnumControl("Type")], [ToggleControl], ...), by their labels.
        private static IEnumerable<Control> Controls(object node)
        {
            foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attribute = property.GetCustomAttributes(true).FirstOrDefault(item => item.GetType().GetInterfaces().Any(face => face.Name == "IControlAttribute"));
                if (attribute == null || property.GetSetMethod(true) == null)
                    continue;
                var labelField = attribute.GetType().GetField("m_Label", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                // "Mask:" on the Swizzle node is the Mask setting.
                var label = (labelField == null ? null : labelField.GetValue(attribute) as string)?.TrimEnd(':');
                var type = property.PropertyType;
                var dropdowns = type.IsValueType && !type.IsEnum && !type.IsPrimitive && type.Namespace != "UnityEngine"
                    ? type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(field => field.FieldType.IsEnum).ToArray()
                    : new FieldInfo[0];
                if (dropdowns.Length > 0)
                {
                    foreach (var field in dropdowns)
                        yield return new Control { label = ObjectNames.NicifyVariableName(field.Name), property = property, field = field };
                    continue;
                }
                yield return new Control { label = string.IsNullOrEmpty(label) ? ObjectNames.NicifyVariableName(property.Name) : label, property = property };
            }
            // Custom Function keeps its settings in the Node Settings tab rather than on the node body.
            if (node.GetType().Name == "CustomFunctionNode")
                foreach (var pair in new[] { new[] { "Type", "sourceType" }, new[] { "Name", "functionName" }, new[] { "Body", "functionBody" }, new[] { "Source", "functionSource" } })
                    yield return new Control { label = pair[0], property = node.GetType().GetProperty(pair[1]) };
        }

        // The Source field holds the .hlsl file's GUID, as picking the file in Node Settings does.
        private static string SourceGuid(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
                throw new ArgumentException("HLSL file was not found: " + path);
            return guid;
        }

        // "In: Vector3, Scale: Float" as in the Node Settings lists; same names keep their slot and wires.
        private static string SetSlotList(object graph, object node, bool input, string raw)
        {
            var slotTypes = Sg.Type("UnityEditor.ShaderGraph.SlotValueType");
            var direction = Sg.Enum("UnityEditor.Graphing.SlotType", input ? "Input" : "Output");
            var model = new Model(graph);
            var current = input ? model.Inputs(node) : model.Outputs(node);
            var other = input ? model.Outputs(node) : model.Inputs(node);
            var nextId = model.Inputs(node).Concat(model.Outputs(node)).Select(slot => (int)Sg.Get(slot, "id")).DefaultIfEmpty(-1).Max() + 1;
            var keep = other.Select(slot => (int)Sg.Get(slot, "id")).ToList();
            var slots = new List<object>();
            foreach (var entry in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(':');
                if (parts.Length != 2)
                    throw new FormatException("Use \"Name: Type, ...\": " + raw);
                var name = parts[0].Trim();
                var typeName = AssetViews.KeyIs(parts[1], "Float") ? "Vector1" : parts[1].Trim();
                var typeValue = System.Enum.GetNames(slotTypes).FirstOrDefault(item => AssetViews.KeyIs(typeName, item));
                if (typeValue == null)
                    throw new ArgumentException("Slot type: Float, " + string.Join(", ", System.Enum.GetNames(slotTypes)));
                var existing = current.FirstOrDefault(slot => SlotName(slot) == name);
                var id = existing == null ? nextId++ : (int)Sg.Get(existing, "id");
                keep.Add(id);
                slots.Add(Sg.CallStatic(Sg.Type("UnityEditor.ShaderGraph.MaterialSlot"), "CreateMaterialSlot", System.Enum.Parse(slotTypes, typeValue), id, name,
                    Sg.CallStatic(Sg.Type("UnityEditor.Graphing.NodeUtils"), "GetHLSLSafeName", name), direction, Vector4.zero, Sg.Enum("UnityEditor.ShaderGraph.ShaderStageCapability", "All"), false));
            }
            Undo(graph, "Change Custom Function Slots");
            foreach (var slot in slots)
                Sg.Call(node, "AddSlot", slot, true);
            Sg.Call(node, "RemoveSlotsNameNotMatching", keep, false);
            Sg.Call(node, "Dirty", Sg.Enum("UnityEditor.Graphing.ModificationScope", "Topological"));
            var fresh = new Model(graph);
            return string.Join(", ", (input ? fresh.Inputs(node) : fresh.Outputs(node)).Select(slot => SlotName(slot) + ": " + TypeName(slot)));
        }

        private static object ControlValue(object node, Control control)
        {
            var property = control.property;
            var value = property.GetValue(node, null);
            if (control.field != null)
                return Printable(control.field.GetValue(value));
            if (property.Name == "functionBody" || property.Name == "functionSource")
            {
                var file = Sg.Get(node, "sourceType").ToString() == "File";
                if (file != (property.Name == "functionSource"))
                    return null;
                if (file)
                    return string.IsNullOrEmpty(value as string) ? "None" : AssetDatabase.GUIDToAssetPath((string)value) is var path && path.Length > 0 ? path : value;
            }
            if (value != null && value.GetType().Name == "ToggleData")
                return Sg.Get(value, "isOn");
            return Printable(value);
        }

        // A popup control (Position.Space) takes the shown entry; its index is what the window stores.
        private static object PopupChoice(string label, object current, string raw)
        {
            var entries = (string[])Sg.Get(current, "popupEntries");
            var index = Array.FindIndex(entries, entry => AssetViews.KeyIs(raw, entry));
            if (index < 0)
                throw new ArgumentException(label + ": " + string.Join(", ", entries));
            Sg.Set(current, "selectedEntry", index);
            return current;
        }

        private static object Parse(Type type, string raw)
        {
            if (type.IsEnum)
                return ParseEnum(type, raw);
            if (type == typeof(bool))
                return AssetViews.Bool(raw);
            if (type == typeof(float))
                return Numbers(raw, 1)[0];
            if (type == typeof(int))
                return AssetViews.Int(raw);
            if (type == typeof(string))
                return raw;
            if (type == typeof(Vector2))
            {
                var n = Numbers(raw, 2);
                return new Vector2(n[0], n[1]);
            }
            if (type == typeof(Vector3))
            {
                var n = Numbers(raw, 3);
                return new Vector3(n[0], n[1], n[2]);
            }
            if (type == typeof(Vector4))
            {
                var n = Numbers(raw, 4);
                return new Vector4(n[0], n[1], n[2], n[3]);
            }
            if (type == typeof(Color))
            {
                var n = Numbers(raw, 4);
                return new Color(n[0], n[1], n[2], n[3]);
            }
            if (type.Name == "ToggleData")
                return Activator.CreateInstance(type, AssetViews.Bool(raw));
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                return AssetViews.KeyIs(raw, "None") ? null : LoadAsset<UnityEngine.Object>(raw);
            throw new NotSupportedException("Values of type " + type.Name + " are edited in the window.");
        }

        private static object ParseEnum(Type type, string raw)
        {
            var name = Enum.GetNames(type).FirstOrDefault(item => AssetViews.KeyIs(raw, item, ObjectNames.NicifyVariableName(item)));
            if (name == null)
                throw new ArgumentException(type.Name + ": " + string.Join(", ", Enum.GetNames(type)));
            return Enum.Parse(type, name);
        }

        // "0.5" fills every component, "1,0,0" leaves the rest at 0 (alpha at 1 for colors is given explicitly).
        private static float[] Numbers(string raw, int count)
        {
            var parts = raw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => float.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : float.NaN).ToArray();
            if (parts.Length == 0 || parts.Any(float.IsNaN))
                throw new FormatException("Expected numbers, <node>.<output> or a Blackboard property: " + raw);
            return Enumerable.Range(0, count).Select(index => parts.Length == 1 ? parts[0] : index < parts.Length ? parts[index] : 0f).ToArray();
        }

        private static T LoadAsset<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new ArgumentException(typeof(T).Name + " was not found: " + path);
            return asset;
        }

        private static string Printable(object value)
        {
            if (value == null)
                return "None";
            if (value is float)
                return Math.Round((float)value, 4).ToString("R", CultureInfo.InvariantCulture);
            if (value is Vector2)
                return AssetViews.Numbers(((Vector2)value).x, ((Vector2)value).y);
            if (value is Vector3)
                return AssetViews.Numbers(((Vector3)value).x, ((Vector3)value).y, ((Vector3)value).z);
            if (value is Vector4)
                return AssetViews.Numbers(((Vector4)value).x, ((Vector4)value).y, ((Vector4)value).z, ((Vector4)value).w);
            if (value is Color)
                return AssetViews.Numbers(((Color)value).r, ((Color)value).g, ((Color)value).b, ((Color)value).a);
            if (value is bool)
                return (bool)value ? "true" : "false";
            if (value is UnityEngine.Object)
                return AssetViews.ObjectLabel((UnityEngine.Object)value) ?? "None";
            if (value.GetType().Name == "ToggleData")
                return AssetViews.Printable(Sg.Get(value, "isOn"));
            if (value.GetType().Name == "PopupList")
            {
                var entries = (string[])Sg.Get(value, "popupEntries");
                var index = (int)Sg.Get(value, "selectedEntry");
                return index >= 0 && index < entries.Length ? entries[index] : index.ToString(CultureInfo.InvariantCulture);
            }
            if (Sg.Has(value, "texture") && !(value is string))
                return Printable(Sg.Get(value, "texture"));
            if (value is Enum)
                return ObjectNames.NicifyVariableName(value.ToString());
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string SlotName(object slot)
        {
            return (string)Sg.Call(slot, "RawDisplayName");
        }

        private static int VectorSize(object slot)
        {
            var type = SlotType(slot);
            return type.StartsWith("Vector", StringComparison.Ordinal) ? type[type.Length - 1] - '0' : 0;
        }

        private static string SlotType(object slot)
        {
            return Sg.Get(slot, "concreteValueType").ToString();
        }

        private static object SlotValue(object slot)
        {
            // UV, Position, Normal and Screen Position inputs show a channel or space, not their hidden default value.
            foreach (var name in new[] { "channel", "space", "screenSpaceType" })
                if (Sg.Has(slot, name))
                    return Printable(Sg.Get(slot, name));
            if (Sg.Has(slot, "value"))
            {
                var value = Sg.Get(slot, "value");
                var size = VectorSize(slot);
                if (value is Matrix4x4)
                    return size == 0 ? null : AssetViews.Numbers(Enumerable.Range(0, size).Select(index => ((Matrix4x4)value).GetRow(0)[index]).ToArray());
                return value is Gradient ? null : Printable(value);
            }
            if (Sg.Has(slot, "texture"))
                return Printable(Sg.Get(slot, "texture"));
            foreach (var name in new[] { "channel", "space", "screenSpaceType", "defaultType" })
                if (Sg.Has(slot, name))
                    return Printable(Sg.Get(slot, name));
            return null;
        }

        // Graph snapshot with stable short ids: the shortest objectId prefix (4+ chars) that is unique in the graph
        // and does not read as a number ("1234", "1e10"), so the graph code tells ids from values.
        private sealed class Model
        {
            internal readonly object graph;
            internal readonly List<object> nodes;
            private readonly List<object> edges;
            private readonly Dictionary<object, string> ids = new Dictionary<object, string>();
            private static readonly System.Text.RegularExpressions.Regex NumberLike = new System.Text.RegularExpressions.Regex(@"^\d+([eE]\d+)?[fFhH]?$");

            internal Model(object graph)
            {
                this.graph = graph;
                nodes = Sg.Items(Sg.CallGeneric(graph, "GetNodes", Sg.Type("UnityEditor.ShaderGraph.AbstractMaterialNode"))).ToList();
                edges = Sg.Items(Sg.Get(graph, "edges")).ToList();
                var objectIds = nodes.Select(node => (string)Sg.Get(node, "objectId")).ToList();
                for (var index = 0; index < nodes.Count; index++)
                {
                    var length = 4;
                    while (length < objectIds[index].Length && (NumberLike.IsMatch(objectIds[index].Substring(0, length)) ||
                        objectIds.Where((other, position) => position != index).Any(other => other.StartsWith(objectIds[index].Substring(0, length), StringComparison.Ordinal))))
                        length++;
                    ids[nodes[index]] = objectIds[index].Substring(0, Math.Min(length, objectIds[index].Length));
                }
            }

            internal string Id(object node)
            {
                return ids[node];
            }

            internal string Label(object node)
            {
                return IsBlock(node) ? (string)Sg.Get(Sg.Get(node, "descriptor"), "displayName") : Id(node) + " " + Sg.Get(node, "name");
            }

            internal bool IsBlock(object node)
            {
                return node.GetType().Name == "BlockNode";
            }

            internal object TryFind(string key)
            {
                key = key.Trim();
                var byId = nodes.Where(node => key.Length >= 4 && ((string)Sg.Get(node, "objectId")).StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byId.Count == 1)
                    return byId[0];
                var space = key.IndexOf(' ');
                if (space > 0)
                {
                    var labelled = nodes.Where(node => AssetViews.KeyIs(key, Label(node))).ToList();
                    if (labelled.Count == 1)
                        return labelled[0];
                }
                var byName = nodes.Where(node => AssetViews.KeyIs(key, (string)Sg.Get(node, "name")) || IsBlock(node) && AssetViews.KeyIs(key, Label(node)) ||
                    Sg.Has(node, "property") && Sg.Get(node, "property") != null && AssetViews.KeyIs(key, (string)Sg.Get(Sg.Get(node, "property"), "displayName"))).ToList();
                return byName.Count == 1 ? byName[0] : null;
            }

            internal object Find(string key)
            {
                var node = TryFind(key);
                if (node != null)
                    return node;
                var same = nodes.Where(item => AssetViews.KeyIs(key, (string)Sg.Get(item, "name"))).ToList();
                throw new ArgumentException(same.Count > 1
                    ? "Several nodes are named " + key + "; use the id: " + string.Join(", ", same.Select(Label))
                    : "Node was not found: " + key);
            }

            internal List<object> Inputs(object node)
            {
                return Slots(node, "GetInputSlots");
            }

            internal List<object> Outputs(object node)
            {
                return Slots(node, "GetOutputSlots");
            }

            private static List<object> Slots(object node, string method)
            {
                var slotType = Sg.Type("UnityEditor.ShaderGraph.MaterialSlot");
                var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(slotType));
                Sg.CallGeneric(node, method, slotType, list);
                return Sg.Items(list).Where(slot => !(bool)Sg.Get(slot, "hidden")).ToList();
            }

            internal IEnumerable<object> IncomingEdges(object node, object slot)
            {
                var id = (int)Sg.Get(slot, "id");
                return edges.Where(edge =>
                {
                    var input = Sg.Get(edge, "inputSlot");
                    return Sg.Get(input, "node") == node && (int)Sg.Get(input, "slotId") == id;
                });
            }

            internal string InputText(object node, object slot)
            {
                var edge = IncomingEdges(node, slot).FirstOrDefault();
                if (edge == null)
                    return SlotValue(slot) as string;
                var output = Sg.Get(edge, "outputSlot");
                var source = Sg.Get(output, "node");
                var sourceSlot = Sg.Get(output, "slot");
                return "← " + (nodes.Contains(source) ? Id(source) : "?") + "." + (sourceSlot == null ? Sg.Get(output, "slotId").ToString() : SlotName(sourceSlot));
            }

            internal string[] OutputTargets(object node, object slot)
            {
                var id = (int)Sg.Get(slot, "id");
                return edges.Where(edge =>
                {
                    var output = Sg.Get(edge, "outputSlot");
                    return Sg.Get(output, "node") == node && (int)Sg.Get(output, "slotId") == id;
                }).Select(edge =>
                {
                    var input = Sg.Get(edge, "inputSlot");
                    var target = Sg.Get(input, "node");
                    var targetSlot = Sg.Get(input, "slot");
                    return "→ " + (IsBlock(target) ? Label(target) : Id(target) + "." + (targetSlot == null ? "?" : SlotName(targetSlot)));
                }).ToArray();
            }

            internal IEnumerable<string> EdgeKeys()
            {
                return edges.Select(edge =>
                {
                    var output = Sg.Get(edge, "outputSlot");
                    var input = Sg.Get(edge, "inputSlot");
                    return Sg.Get(Sg.Get(output, "node"), "objectId") + ":" + Sg.Get(output, "slotId") + ">" + Sg.Get(Sg.Get(input, "node"), "objectId") + ":" + Sg.Get(input, "slotId");
                }).OrderBy(key => key, StringComparer.Ordinal);
            }

            // Nodes this node feeds, with the row of the input it feeds.
            internal IEnumerable<KeyValuePair<object, int>> Targets(object node)
            {
                foreach (var edge in edges)
                {
                    if (Sg.Get(Sg.Get(edge, "outputSlot"), "node") != node)
                        continue;
                    var input = Sg.Get(edge, "inputSlot");
                    var target = Sg.Get(input, "node");
                    if (!nodes.Contains(target))
                        continue;
                    var slotId = (int)Sg.Get(input, "slotId");
                    var row = Inputs(target).FindIndex(slot => (int)Sg.Get(slot, "id") == slotId);
                    yield return new KeyValuePair<object, int>(target, Mathf.Max(row, 0));
                }
            }

            internal IEnumerable<object> Neighbours(object node)
            {
                foreach (var edge in edges)
                {
                    var from = Sg.Get(Sg.Get(edge, "outputSlot"), "node");
                    var to = Sg.Get(Sg.Get(edge, "inputSlot"), "node");
                    if (from == node)
                        yield return to;
                    if (to == node)
                        yield return from;
                }
            }

            // Downstream path between two nodes; a block or null end means "up to the output".
            internal List<object> Chain(object start, object end)
            {
                var previous = new Dictionary<object, object> { { start, null } };
                var queue = new Queue<object>();
                queue.Enqueue(start);
                object reached = null;
                while (queue.Count > 0 && reached == null)
                {
                    var current = queue.Dequeue();
                    foreach (var edge in edges.Where(item => Sg.Get(Sg.Get(item, "outputSlot"), "node") == current))
                    {
                        var next = Sg.Get(Sg.Get(edge, "inputSlot"), "node");
                        if (previous.ContainsKey(next))
                            continue;
                        previous[next] = current;
                        if (next == end || end == null && IsBlock(next))
                        {
                            reached = next;
                            break;
                        }
                        queue.Enqueue(next);
                    }
                }
                if (reached == null)
                    throw new ArgumentException("No connection leads from " + Label(start) + " to " + (end == null ? "the output" : Label(end)) + ".");
                var chain = new List<object>();
                for (var node = reached; node != null; node = previous[node])
                    chain.Insert(0, IsBlock(node) ? null : node);
                if (chain.Last() != null && (end == null || IsBlock(end)))
                    chain.Add(null);
                return chain.Distinct().ToList();
            }

            internal List<string> NodeErrors(object node)
            {
                var result = new List<string>();
                var messages = Sg.Get(graph, "messageManager");
                if (messages == null)
                    return result;
                foreach (var pair in Sg.Items(Sg.Call(messages, "GetNodeMessages")))
                    if ((string)Sg.Get(pair, "Key") == (string)Sg.Get(node, "objectId"))
                        result.AddRange(Sg.Items(Sg.Get(pair, "Value")).Select(message => FirstLine((string)Sg.Get(message, "message"))));
                return result;
            }
        }
    }
}
