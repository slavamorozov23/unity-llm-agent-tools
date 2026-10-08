using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class SceneScreenshotService
    {
        private const float Margin = 1.15f;

        // A pointer for the agent when another mode shows the same thing better.
        internal static string Note { get; private set; }

        public static string[] Capture(string[] paths, string mode, string from, out string[] labels)
        {
            labels = Array.Empty<string>();
            Note = null;
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("At least one scene object is required.", "paths");
            var targets = paths.Distinct(StringComparer.Ordinal).Select(ScenePath.ResolveObject).ToArray();
            var overlayOnly = targets.All(IsOverlayUiOnly);
            var source = FindSourceCamera();
            if (source == null)
                throw new InvalidOperationException("The current scene has no camera to copy.");
            targets = targets.Where(target => IsVisibleBy(source, target)).ToArray();
            if (targets.Length == 0)
                throw new InvalidOperationException("The main camera does not render any selected scene object.");
            Canvas.ForceUpdateCanvases();
            var bounds = CombinedBounds(targets);
            var resolution = GameResolutionService.Current();
            var cameraObject = new GameObject("Unity Agent Bridge Scene Camera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            Camera camera;
            Canvas[] overlayCanvases;
            OverlayCanvasState[] overlayStates;
            try
            {
                camera = cameraObject.AddComponent<Camera>();
                EditorUtility.CopySerialized(source, camera);
                camera.enabled = false;
                camera.targetTexture = null;
                overlayCanvases = overlayOnly
                    ? targets.SelectMany(target => target.GetComponentsInParent<Canvas>(true))
                        .Concat(targets.SelectMany(target => target.GetComponentsInChildren<Canvas>(true)))
                        .Where(canvas => canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                        .Distinct()
                        .ToArray()
                    : Array.Empty<Canvas>();
                overlayStates = overlayCanvases.Select(canvas => new OverlayCanvasState(canvas)).ToArray();
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                throw;
            }

            try
            {
                foreach (var canvas in overlayCanvases)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = camera;
                    canvas.planeDistance = Mathf.Max(camera.nearClipPlane + 0.1f, 1f);
                }
                if (overlayOnly)
                {
                    Canvas.ForceUpdateCanvases();
                    return new[] { Render(camera, resolution, "scene-overlay.png") };
                }
                if (!string.IsNullOrWhiteSpace(from))
                {
                    ConfigureFrom(camera, Viewpoint(from), bounds, resolution.width, resolution.height);
                    return new[] { Render(camera, resolution, "scene-from.png") };
                }
                if (string.Equals(mode, "flat", StringComparison.OrdinalIgnoreCase))
                {
                    ConfigureFront(camera, source, bounds, resolution.width, resolution.height);
                    // A floor or a level seen from the side is a strip; the developer would click the top gizmo.
                    if (bounds.extents.y < Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.5f)
                        Note = "flat снимает сбоку; вид сверху — кадр top в mode grid.";
                    return new[] { Render(camera, resolution, "scene-flat.png") };
                }
                if (!string.Equals(mode, "grid", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Scene screenshot mode must be grid or flat.", "mode");
                if (!HasThreeDimensionalGeometry(targets))
                {
                    var closeUps = targets.Take(4).ToArray();
                    labels = closeUps.Select(SceneLabel).ToArray();
                    return closeUps.Select((target, index) =>
                    {
                        ConfigureFront(camera, source, ObjectBounds(target), resolution.width, resolution.height);
                        return Render(camera, resolution, "scene-flat-" + index + ".png");
                    }).ToArray();
                }

                ConfigureBestPerspective(camera, source, targets, bounds, resolution.width, resolution.height);
                var main = Render(camera, resolution, "scene-main.png");
                ConfigureTop(camera, bounds, resolution.width, resolution.height, false);
                var top = Render(camera, resolution, "scene-top.png");
                ConfigureTop(camera, bounds, resolution.width, resolution.height, true);
                var bottom = Render(camera, resolution, "scene-bottom.png");
                ConfigureFront(camera, source, bounds, resolution.width, resolution.height);
                var flat = Render(camera, resolution, "scene-flat.png");
                return new[] { main, top, bottom, flat };
            }
            finally
            {
                foreach (var state in overlayStates)
                    state.Restore();
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private sealed class OverlayCanvasState
        {
            private readonly Canvas canvas;
            private readonly RenderMode renderMode;
            private readonly Camera worldCamera;
            private readonly float planeDistance;

            public OverlayCanvasState(Canvas value)
            {
                canvas = value;
                renderMode = value.renderMode;
                worldCamera = value.worldCamera;
                planeDistance = value.planeDistance;
            }

            public void Restore()
            {
                if (canvas == null)
                    return;
                canvas.renderMode = renderMode;
                canvas.worldCamera = worldCamera;
                canvas.planeDistance = planeDistance;
            }
        }

        private static bool HasThreeDimensionalGeometry(GameObject[] targets)
        {
            foreach (var target in targets)
            {
                if (target.GetComponentsInChildren<Terrain>(false).Any(terrain => terrain.enabled))
                    return true;
                if (target.GetComponentsInChildren<Renderer>(false)
                    .Any(renderer => renderer.enabled && !(renderer is SpriteRenderer)))
                    return true;
            }
            return false;
        }

        private static bool IsOverlayUiOnly(GameObject target)
        {
            if (target.GetComponentsInChildren<Renderer>(false).Any(renderer => renderer.enabled) ||
                target.GetComponentsInChildren<Terrain>(false).Any(terrain => terrain.enabled))
                return false;
            var renderers = target.GetComponentsInChildren<CanvasRenderer>(false)
                .Where(renderer => renderer.gameObject.activeInHierarchy)
                .ToArray();
            return renderers.Length > 0 && renderers.All(renderer =>
            {
                var canvas = renderer.GetComponentInParent<Canvas>();
                return canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay;
            });
        }

        private static string SceneLabel(GameObject target)
        {
            var fullPath = ScenePath.For(target);
            var separator = fullPath.IndexOf('/', 1);
            var scenePath = separator < 0 ? "/" + target.name : fullPath.Substring(separator);
            return target.scene.name + ":" + scenePath;
        }

        private static bool IsVisibleBy(Camera camera, GameObject target)
        {
            if (target.GetComponentsInChildren<Renderer>(false)
                .Any(renderer => renderer.enabled && (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0))
                return true;
            if (target.GetComponentsInChildren<CanvasRenderer>(false)
                .Any(renderer => renderer.gameObject.activeInHierarchy && (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0))
                return true;
            return target.GetComponentsInChildren<Terrain>(false)
                .Any(terrain => terrain.enabled && (camera.cullingMask & (1 << terrain.gameObject.layer)) != 0);
        }

        private static Camera FindSourceCamera()
        {
            var cameras = new List<Camera>();
            foreach (var scene in ScenePath.ContextScenes())
                foreach (var root in scene.GetRootGameObjects())
                    cameras.AddRange(root.GetComponentsInChildren<Camera>(true));
            return cameras.FirstOrDefault(item => item.CompareTag("MainCamera"))
                ?? cameras.FirstOrDefault(item => item.enabled)
                ?? cameras.FirstOrDefault();
        }

        private static Bounds CombinedBounds(GameObject[] targets)
        {
            var initialized = false;
            var combined = new Bounds();
            foreach (var target in targets)
            {
                var targetBounds = ObjectBounds(target);
                if (!initialized)
                {
                    combined = targetBounds;
                    initialized = true;
                }
                else
                    combined.Encapsulate(targetBounds);
            }
            if (!initialized)
                throw new InvalidOperationException("Selected scene objects have no bounds.");
            var size = combined.size;
            combined.Expand(new Vector3(
                Mathf.Max(0.01f, 0.01f - size.x),
                Mathf.Max(0.01f, 0.01f - size.y),
                Mathf.Max(0.01f, 0.01f - size.z)));
            return combined;
        }

        private static Bounds ObjectBounds(GameObject target)
        {
            var initialized = false;
            var result = new Bounds(target.transform.position, Vector3.zero);
            foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
                Encapsulate(ref result, ref initialized, renderer.bounds);
            foreach (var collider in target.GetComponentsInChildren<Collider>(true))
                Encapsulate(ref result, ref initialized, collider.bounds);
            foreach (var collider in target.GetComponentsInChildren<Collider2D>(true))
                Encapsulate(ref result, ref initialized, collider.bounds);
            foreach (var terrain in target.GetComponentsInChildren<Terrain>(true))
                if (terrain.terrainData != null)
                    Encapsulate(ref result, ref initialized, TransformBounds(terrain.terrainData.bounds, terrain.transform));
            foreach (var rect in target.GetComponentsInChildren<RectTransform>(true))
            {
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                foreach (var corner in corners)
                    Encapsulate(ref result, ref initialized, new Bounds(corner, Vector3.zero));
            }
            return initialized ? result : new Bounds(target.transform.position, Vector3.one * 0.1f);
        }

        private static Bounds TransformBounds(Bounds localBounds, Transform transform)
        {
            var center = transform.TransformPoint(localBounds.center);
            var extents = localBounds.extents;
            var axisX = transform.TransformVector(new Vector3(extents.x, 0f, 0f));
            var axisY = transform.TransformVector(new Vector3(0f, extents.y, 0f));
            var axisZ = transform.TransformVector(new Vector3(0f, 0f, extents.z));
            var worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, worldExtents * 2f);
        }

        private static void Encapsulate(ref Bounds result, ref bool initialized, Bounds value)
        {
            if (!initialized)
            {
                result = value;
                initialized = true;
            }
            else
                result.Encapsulate(value);
        }

        private static void ConfigureBestPerspective(Camera camera, Camera source, GameObject[] targets, Bounds bounds, int width, int height)
        {
            var renderAspect = width / (float)height;
            var radius = Mathf.Max(0.5f, bounds.extents.magnitude);
            var verticalHalfAngle = Mathf.Max(10f, camera.fieldOfView) * Mathf.Deg2Rad * 0.5f;
            var horizontalHalfAngle = Mathf.Atan(Mathf.Tan(verticalHalfAngle) * renderAspect);
            var limitingHalfAngle = Mathf.Max(1f * Mathf.Deg2Rad, Mathf.Min(verticalHalfAngle, horizontalHalfAngle));
            var distance = radius / Mathf.Sin(limitingHalfAngle) * Margin;
            var center = bounds.center;
            var positions = new List<Vector3>();
            var sourceDirection = source.transform.position - center;
            if (sourceDirection.sqrMagnitude < 0.0001f)
                sourceDirection = -source.transform.forward;
            positions.Add(center + sourceDirection.normalized * distance);
            var directions = new[]
            {
                new Vector3(1f, .65f, -1f), new Vector3(-1f, .65f, -1f),
                new Vector3(1f, .65f, 1f), new Vector3(-1f, .65f, 1f),
                new Vector3(0f, .8f, -1f), new Vector3(0f, .8f, 1f),
                new Vector3(1f, .3f, 0f), new Vector3(-1f, .3f, 0f)
            };
            positions.AddRange(directions.Select(direction => center + direction.normalized * distance));

            var bestScore = int.MinValue;
            var bestPosition = positions[0];
            var bestRotation = Quaternion.identity;
            foreach (var position in positions)
            {
                if ((center - position).sqrMagnitude < 0.0001f)
                    continue;
                camera.transform.position = position;
                camera.transform.rotation = Quaternion.LookRotation(center - position, Vector3.up);
                var score = VisibilityScore(camera, targets);
                if (score <= bestScore)
                    continue;
                bestScore = score;
                bestPosition = position;
                bestRotation = camera.transform.rotation;
            }
            camera.transform.position = bestPosition;
            camera.transform.rotation = bestRotation;
            camera.transform.localScale = Vector3.one;
        }

        private static int VisibilityScore(Camera camera, GameObject[] targets)
        {
            var score = 0;
            foreach (var target in targets)
            {
                var center = ObjectBounds(target).center;
                var viewport = camera.WorldToViewportPoint(center);
                if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                    continue;
                var targetBounds = ObjectBounds(target);
                var direction = targetBounds.center - camera.transform.position;
                var ray = new Ray(camera.transform.position, direction.normalized);
                float targetDistance;
                if (!targetBounds.IntersectRay(ray, out targetDistance))
                    continue;
                var hits = Physics.RaycastAll(ray, direction.magnitude + targetBounds.extents.magnitude + 0.01f)
                    .OrderBy(hit => hit.distance);
                var first = hits.FirstOrDefault();
                if (first.collider == null || first.collider.transform == target.transform ||
                    first.collider.transform.IsChildOf(target.transform) || first.distance >= targetDistance - 0.01f)
                    score++;
            }
            return score;
        }

        private static void ConfigureTop(Camera camera, Bounds bounds, int width, int height, bool bottom)
        {
            camera.orthographic = true;
            camera.aspect = width / (float)height;
            camera.orthographicSize = Mathf.Max(bounds.extents.z, bounds.extents.x / camera.aspect) * Margin;
            var distance = Mathf.Max(1f, bounds.extents.magnitude * 2f);
            camera.transform.position = bounds.center + Vector3.up * (bottom ? -distance : distance);
            camera.transform.rotation = Quaternion.LookRotation(bottom ? Vector3.up : Vector3.down, Vector3.forward);
            SetClipping(camera, bounds);
        }

        private static void ConfigureFront(Camera camera, Camera source, Bounds bounds, int width, int height)
        {
            camera.orthographic = true;
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.aspect = width / (float)height;
            camera.orthographicSize = Mathf.Max(bounds.extents.y, bounds.extents.x / camera.aspect) * Margin;
            var direction = Mathf.Abs(source.transform.forward.z) > 0.5f
                ? Mathf.Sign(source.transform.forward.z) * Vector3.forward
                : Vector3.forward;
            var distance = Mathf.Max(1f, bounds.extents.magnitude * 2f);
            var position = bounds.center - direction * distance;
            position.z = source.transform.position.z;
            if (Mathf.Abs(position.z - bounds.center.z) < 0.01f)
                position.z = bounds.center.z - direction.z * distance;
            camera.transform.position = position;
            camera.transform.rotation = Quaternion.LookRotation(bounds.center - position, Vector3.up);
            SetClipping(camera, bounds);
        }

        // A point x,y,z, or an object's centre: the room the developer flies into in the Scene view to see its walls.
        private static Vector3 Viewpoint(string from)
        {
            var numbers = from.Split(',');
            float x, y, z;
            if (numbers.Length == 3 &&
                float.TryParse(numbers[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) &&
                float.TryParse(numbers[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y) &&
                float.TryParse(numbers[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z))
                return new Vector3(x, y, z);
            var target = ScenePath.ResolveObject(from);
            var bounds = ObjectBounds(target);
            return bounds.size == Vector3.zero ? target.transform.position : bounds.center;
        }

        // Perspective from the viewpoint at the target, wide enough to hold it; nothing is clipped, the walls around count.
        private static void ConfigureFrom(Camera camera, Vector3 position, Bounds bounds, int width, int height)
        {
            camera.orthographic = false;
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.aspect = width / (float)height;
            var direction = bounds.center - position;
            if (direction.sqrMagnitude < 0.0001f)
                direction = camera.transform.forward;
            camera.transform.position = position;
            camera.transform.rotation = Quaternion.LookRotation(direction, Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up);
            camera.transform.localScale = Vector3.one;
            var angle = 0f;
            foreach (var corner in Corners(bounds))
            {
                var local = camera.transform.InverseTransformPoint(corner);
                if (local.z <= 0.01f)
                {
                    angle = 179f;
                    break;
                }
                angle = Mathf.Max(angle, Mathf.Atan(Mathf.Abs(local.y) / local.z), Mathf.Atan(Mathf.Abs(local.x) / local.z / camera.aspect));
            }
            camera.fieldOfView = Mathf.Clamp(angle * Mathf.Rad2Deg * 2f * 1.1f, 30f, 100f);
            camera.nearClipPlane = 0.02f;
            camera.farClipPlane = Mathf.Max(100f, Vector3.Distance(position, bounds.center) + bounds.extents.magnitude * 2f);
        }

        private static IEnumerable<Vector3> Corners(Bounds bounds)
        {
            for (var i = 0; i < 8; i++)
                yield return bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
        }

        private static void SetClipping(Camera camera, Bounds bounds)
        {
            var distance = Vector3.Distance(camera.transform.position, bounds.center);
            var radius = Mathf.Max(0.1f, bounds.extents.magnitude);
            // Only the target's bounding sphere is kept, so floors, ceilings and walls around it do not cover it.
            camera.nearClipPlane = Mathf.Max(0.01f, distance - radius * 1.05f);
            camera.farClipPlane = Mathf.Max(camera.nearClipPlane + 1f, distance + radius * 1.05f);
        }

        private static string Render(Camera camera, GameResolutionData resolution, string fileName)
        {
            var renderTexture = RenderTexture.GetTemporary(resolution.width, resolution.height, 24, RenderTextureFormat.ARGB32);
            var previousActive = RenderTexture.active;
            var previousTarget = camera.targetTexture;
            Texture2D texture = null;
            // Shaders still compiling would draw as cyan placeholders; the frame waits for them, as Unity Recorder does.
            var asyncCompilation = ShaderUtil.allowAsyncCompilation;
            try
            {
                camera.targetTexture = renderTexture;
                ShaderUtil.allowAsyncCompilation = false;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture = new Texture2D(resolution.width, resolution.height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0f, 0f, resolution.width, resolution.height), 0, 0, false);
                texture.Apply(false, false);
                var directory = Path.Combine(BridgePaths.RuntimeRoot, "Screenshots");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, texture.EncodeToPNG());
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                return path;
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncCompilation;
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

    }
}
