using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // The preview under a material's Inspector, one image per shape button (Sphere, Cube, Cylinder, Torus, Quad), and a Plane.
    // A shader or Shader Graph is previewed with a new material of it, as the Inspector shows its default values;
    // a scene object with its renderer's material as it draws now, MaterialPropertyBlock (textures set by scripts) included.
    internal static class MaterialPreviewService
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        internal static readonly string[] Shapes = { "Sphere", "Cube", "Cylinder", "Torus", "Quad", "Plane" };

        internal static string Capture(BridgeRequest request)
        {
            var shapes = (request.names ?? new string[0]).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
            if (shapes.Count == 0)
                shapes.Add("Sphere");
            var size = request.width > 0 ? Mathf.Clamp(request.width, 64, 1024) : 256;
            var screenshots = Render(request.path, shapes, size, "material-");
            var labels = shapes.Select(shape => Shapes.First(item => AssetViews.KeyIs(shape, item))).ToList();
            return new JsonText().Add("screenshots", screenshots).Add("labels", labels).ToString();
        }

        internal static List<string> Render(string path, IList<string> shapes, int size, string prefix)
        {
            var material = MaterialOf(path);
            var screenshots = new List<string>();
            MaterialEditor editor = null;
            Mesh plane = null;
            try
            {
                editor = (MaterialEditor)UnityEditor.Editor.CreateEditor(material);
                // The Inspector's interactive preview: RenderStaticPreview (Project thumbnails) always draws a sphere.
                var type = typeof(MaterialEditor);
                type.GetMethod("Init", Members, null, Type.EmptyTypes, null).Invoke(editor, null);
                var selected = type.GetField("m_SelectedMesh", Members);
                var utility = (PreviewRenderUtility)type.GetMethod("GetPreviewRendererUtility", Members).Invoke(editor, null);
                var render = type.GetMethod("DoRenderPreview", Members);
                foreach (var shape in shapes)
                {
                    var index = Array.FindIndex(Shapes, item => AssetViews.KeyIs(shape, item));
                    if (index < 0)
                        throw new ArgumentException("Shape: " + string.Join(", ", Shapes));
                    utility.BeginStaticPreview(new Rect(0, 0, size, size));
                    if (Shapes[index] == "Plane")
                        RenderPlane(utility, material, plane = plane ?? PlaneMesh());
                    else
                    {
                        selected.SetValue(editor, index);
                        render.Invoke(editor, new object[] { utility, false });
                    }
                    var texture = utility.EndStaticPreview();
                    if (texture == null)
                        throw new InvalidOperationException("Unity rendered no preview for " + path + ".");
                    try
                    {
                        var directory = Path.Combine(BridgePaths.RuntimeRoot, "Screenshots");
                        Directory.CreateDirectory(directory);
                        var file = Path.Combine(directory, prefix + screenshots.Count + ".png");
                        File.WriteAllBytes(file, texture.EncodeToPNG());
                        screenshots.Add(file);
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
            }
            finally
            {
                if (editor != null)
                    UnityEngine.Object.DestroyImmediate(editor);
                if (plane != null)
                    UnityEngine.Object.DestroyImmediate(plane);
                if (!AssetDatabase.Contains(material))
                    UnityEngine.Object.DestroyImmediate(material);
            }
            return screenshots;
        }

        // Unity's Plane (GameObject > 3D Object > Plane) lying in world XZ, seen from above at an angle, for ground,
        // water and world-space projected shaders that read as nothing on the Inspector's shapes.
        private static Mesh PlaneMesh()
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Plane);
            try
            {
                var mesh = UnityEngine.Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
                mesh.hideFlags = HideFlags.HideAndDontSave;
                return mesh;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(primitive);
            }
        }

        private static void RenderPlane(PreviewRenderUtility utility, Material material, Mesh mesh)
        {
            var camera = utility.camera;
            camera.fieldOfView = 40f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.transform.position = new Vector3(0f, 7.5f, -7.5f);
            camera.transform.rotation = Quaternion.Euler(45f, 0f, 0f);
            utility.lights[0].intensity = 1.4f;
            utility.lights[0].transform.rotation = Quaternion.Euler(50f, 50f, 0f);
            utility.lights[1].intensity = 1.4f;
            utility.ambientColor = new Color(0.1f, 0.1f, 0.1f, 0f);
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                utility.DrawMesh(mesh, Matrix4x4.identity, material, submesh);
            utility.Render(true);
        }

        private static Material MaterialOf(string path)
        {
            if (path.StartsWith("/", StringComparison.Ordinal))
                return LiveMaterial(SceneRenderer(path));
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var material = asset as Material;
            if (material != null)
            {
                EditorPresentationService.ShowAsset(material);
                return material;
            }
            var shader = asset as Shader;
            if (shader == null)
                throw new ArgumentException("Preview needs a material, shader or Shader Graph: " + path);
            return new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = shader.name };
        }

        internal static Renderer SceneRenderer(string path)
        {
            var renderer = ScenePath.ResolveObject(path).GetComponent<Renderer>();
            if (renderer == null || renderer.sharedMaterial == null)
                throw new ArgumentException("Preview needs a Renderer with a material on " + path + ".");
            return renderer;
        }

        internal static Material LiveMaterial(Renderer renderer)
        {
            var material = new Material(renderer.sharedMaterial) { hideFlags = HideFlags.HideAndDontSave, name = renderer.sharedMaterial.name };
            if (!renderer.HasPropertyBlock())
                return material;
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            var shader = material.shader;
            for (var index = 0; index < shader.GetPropertyCount(); index++)
            {
                var name = shader.GetPropertyName(index);
                switch (shader.GetPropertyType(index))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        if (block.HasTexture(name))
                            material.SetTexture(name, block.GetTexture(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        if (block.HasColor(name))
                            material.SetColor(name, block.GetColor(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        if (block.HasVector(name))
                            material.SetVector(name, block.GetVector(name));
                        break;
                    default:
                        if (block.HasFloat(name))
                            material.SetFloat(name, block.GetFloat(name));
                        break;
                }
            }
            return material;
        }
    }
}
