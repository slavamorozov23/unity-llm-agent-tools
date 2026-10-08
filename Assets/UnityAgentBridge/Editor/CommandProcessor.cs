using System;
using UnityEditor;

namespace UnityAgentBridge.Editor
{
    internal static class CommandProcessor
    {
        private static double playStartRequestedUntil;
        internal static Func<BridgeRequest, string> Timeline;
        internal static Func<BridgeRequest, string> ShaderGraphPreview;
        // Package views react to the agent moving on (Shader Graph saves and closes its window).
        internal static Action<BridgeRequest> BeforeCommand;

        public static bool CanExecute(BridgeRequest request)
        {
            if (request == null)
                return true;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return false;
            if (EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                return request.command == "get-status" || request.command == "prepare-game-interaction";

            switch (request.command)
            {
                case "dispatch-game-input":
                    return !EditorApplication.isPlaying || GameInteractionService.CanCompleteDispatch(request);
                case "pause-and-capture-game":
                case "capture-game":
                    return GameInteractionService.ReadyToCapture();
                case "control-animator":
                case "get-animator-runtime-state":
                    return EditorApplication.isPlaying || EditorApplication.timeSinceStartup >= playStartRequestedUntil;
                default:
                    return true;
            }
        }

        public static BridgeResponse Execute(BridgeRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request");
            if (string.IsNullOrWhiteSpace(request.id))
                throw new InvalidOperationException("Request id is required.");
            if (string.IsNullOrWhiteSpace(request.command))
                throw new InvalidOperationException("Request command is required.");

            if (BeforeCommand != null)
                BeforeCommand(request);
            TmpEssentialsService.WatchAfterCommand();
            var response = new BridgeResponse { id = request.id, ok = true };
            var saveAfterMutation = false;
            switch (request.command)
            {
                case "objects-near":
                    response.message = SceneService.Near(request.value, request.query);
                    break;
                case "get-prefab-tree":
                    response.objects = SceneService.GetPrefabTree(request.path);
                    break;
                case "get-scene-tree":
                    response.objects = SceneService.GetTree();
                    break;
                case "get-object-info":
                    response.objectInfo = SceneService.GetObjectInfo(request);
                    break;
                case "resolve-object-path":
                    var resolvedObject = ScenePath.ResolveObject(request.path);
                    EditorPresentationService.ShowSceneObject(resolvedObject);
                    response.message = ScenePath.For(resolvedObject);
                    break;
                case "list-component-types":
                    response.componentTypes = ComponentService.GetAllComponentTypeNames();
                    break;
                case "modify-component":
                    string modifyWarning;
                    response.objectInfo = ComponentService.Modify(request, out modifyWarning);
                    response.message = modifyWarning;
                    saveAfterMutation = true;
                    break;
                case "add-component":
                    string warning;
                    int addedComponentIndex;
                    response.objectInfo = ComponentService.Add(request, out warning, out addedComponentIndex);
                    response.componentIndex = addedComponentIndex;
                    response.message = warning;
                    saveAfterMutation = true;
                    break;
                case "remove-component":
                    response.objectInfo = ComponentService.Remove(request);
                    saveAfterMutation = true;
                    break;
                case "execute-component-action":
                    response.message = InspectorDiagnosticService.Execute(request);
                    saveAfterMutation = true;
                    break;
                case "execute-asset-action":
                    response.message = AssetService.ExecuteAction(request.path, request.action, request.values);
                    break;
                case "delete-object":
                    ObjectService.Delete(request.path);
                    response.message = "Object deleted.";
                    saveAfterMutation = true;
                    break;
                case "duplicate-object":
                    response.objectInfo = ObjectService.Duplicate(request.path, request.name);
                    saveAfterMutation = true;
                    break;
                case "list-prefabs":
                    response.prefabs = ObjectService.ListPrefabs(request.path);
                    break;
                case "create-empty-object":
                    response.objectInfo = PrefabService.CreateEmpty(request.destinationPath, request.name, request.templateName, out response.message);
                    saveAfterMutation = true;
                    break;
                case "save-scenes":
                    ScenePersistenceService.SaveBeforeTransition();
                    response.message = "Open scenes saved.";
                    break;
                case "save-prefab":
                    response.prefabs = new[] { PrefabService.Save(request.path, request.destinationPath) };
                    saveAfterMutation = true;
                    break;
                case "apply-prefab":
                    string applyWarning;
                    response.objectInfo = PrefabService.Apply(
                        request.path, request.componentType, request.componentIndex, request.propertyPath, out applyWarning);
                    response.message = applyWarning;
                    saveAfterMutation = true;
                    break;
                case "instantiate-prefab":
                    response.objectInfo = PrefabService.Instantiate(request.path, request.destinationPath);
                    saveAfterMutation = true;
                    break;
                case "revert-prefab":
                    response.objectInfo = PrefabService.Revert(
                        request.path, request.componentType, request.componentIndex, request.propertyPath);
                    saveAfterMutation = true;
                    break;
                case "open-prefab":
                    response.prefabs = new[] { PrefabService.Open(request.path) };
                    break;
                case "close-prefab":
                    response.prefabs = new[] { PrefabService.Close() };
                    break;
                case "list-scenes":
                    response.scenes = SceneService.ListSceneAssets();
                    break;
                case "open-scene":
                    response.scenes = new[] { SceneService.OpenScene(request.path) };
                    break;
                case "list-creation-templates":
                    response.templates = AssetService.ListCreationTemplates(request.path);
                    break;
                case "list-object-creation-templates":
                    response.objectTemplates = PrefabService.ListCreationTemplates();
                    break;
                case "create-asset":
                    response.assetInfo = AssetService.Create(request.templateName, request.path, request.objectPath);
                    break;
                case "get-asset-info":
                    response.assetInfo = AssetService.GetInfo(request.path, request.propertyPath, request.section);
                    break;
                case "modify-asset":
                    response.assetInfo = AssetService.Modify(request.path, request.values, request.boolValue);
                    break;
                case "get-shader-errors":
                    response.message = AssetService.GetShaderErrors();
                    break;
                case "get-global-shader-properties":
                    response.message = AssetService.GetGlobalShaderProperties(request.propertyPath);
                    break;
                case "get-shader-info":
                    response.message = AssetService.GetShaderInfo(request.path, request.propertyPath);
                    break;
                case "modify-material":
                    response.message = AssetService.ModifyMaterial(request.path, request.values);
                    break;
                case "reimport-asset":
                    response.assetInfo = AssetService.Reimport(request.path, request.boolValue);
                    break;
                case "import-package":
                    response.message = AssetService.ImportPackage(request.path);
                    break;
                case "refresh-assets":
                    response.message = AssetRefreshService.Schedule(request.id);
                    break;
                case "compile-after-play":
                    response.message = AssetRefreshService.CompileAfterPlay();
                    break;
                case "get-sprite-layout":
                    response.message = AssetService.GetSpriteLayout(request.path);
                    break;
                case "mutate-sprite-layout":
                    response.message = AssetService.MutateSpriteLayout(request.path, request.action, request.json);
                    break;
                case "move-asset":
                    response.assetInfo = AssetService.Move(request.path, request.destinationPath);
                    break;
                case "duplicate-asset":
                    response.assetInfo = AssetService.Duplicate(request.path, request.destinationPath);
                    break;
                case "delete-asset":
                    response.message = AssetService.Delete(request.path);
                    break;
                case "asset-object-picker":
                    response.candidates = AssetService.GetObjectPickerCandidates(request.path, request.propertyPath, 10);
                    break;
                case "move-object":
                    response.objectInfo = ObjectService.Move(request.path, request.destinationPath, request.siblingIndex);
                    saveAfterMutation = true;
                    break;
                case "rename-object":
                    response.objectInfo = ObjectService.Rename(request.path, request.destinationPath);
                    saveAfterMutation = true;
                    break;
                case "set-active":
                    response.objectInfo = ObjectService.SetActive(request.path, request.boolValue);
                    saveAfterMutation = true;
                    break;
                case "set-layer":
                    response.message = ObjectService.SetLayer(request.path, request.layer, request.boolValue);
                    saveAfterMutation = !request.path.StartsWith("Assets/", StringComparison.Ordinal);
                    break;
                case "set-static":
                    response.message = ObjectService.SetStatic(request.path, request.value, request.boolValue);
                    saveAfterMutation = !request.path.StartsWith("Assets/", StringComparison.Ordinal);
                    break;
                case "set-tag":
                    response.message = ObjectService.SetTag(request.path, request.name);
                    saveAfterMutation = !request.path.StartsWith("Assets/", StringComparison.Ordinal);
                    break;
                case "object-picker":
                    response.candidates = ComponentService.GetObjectPickerCandidates(request);
                    break;
                case "get-logs":
                    response.logs = DebugService.GetLast(2000);
                    response.currentCompilationErrors = DebugService.GetCurrentCompilationErrors();
                    response.message = DebugService.Marks();
                    EditorPresentationService.ShowWindow("UnityEditor.ConsoleWindow");
                    break;
                case "clear-logs":
                    DebugService.Clear();
                    DebugService.ClearConsole();
                    EditorPresentationService.ShowWindow("UnityEditor.ConsoleWindow");
                    response.message = "Logs cleared.";
                    break;
                case "get-status":
                    response.status = DebugService.Status();
                    response.message = DebugService.StatusJson();
                    break;
                case "list-menu-items":
                    response.items = request.boolValue ? MenuService.ListProject() : MenuService.List();
                    break;
                case "execute-menu-item":
                    response.message = MenuService.Execute(request.path, request.id);
                    break;
                case "lighting":
                    response.message = LightingService.Execute(request.action);
                    break;
                case "profiler-start":
                    response.message = ProfilerService.Start(request.limit, request.value == "Edit Mode");
                    break;
                case "profiler-read":
                    bool profilerPending;
                    response.message = ProfilerService.Read(request.limit, out profilerPending);
                    response.pending = profilerPending;
                    break;
                case "profiler-frames":
                    response.message = ProfilerService.Frames(request.query);
                    break;
                case "profiler-hierarchy":
                    response.message = ProfilerService.Hierarchy(request.value, request.name, request.query, request.path, request.action, request.limit);
                    break;
                case "set-play-mode":
                    bool playTransitionRequested;
                    response.message = DebugService.SetPlayMode(request.action, out playTransitionRequested);
                    playStartRequestedUntil = playTransitionRequested &&
                        (string.Equals(request.action, "start", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(request.action, "запустить", StringComparison.OrdinalIgnoreCase))
                        ? EditorApplication.timeSinceStartup + 120d
                        : 0d;
                    break;
                case "list-game-resolutions":
                    response.resolutions = GameResolutionService.List();
                    response.resolution = GameResolutionService.Selected();
                    break;
                case "set-game-resolution":
                    response.resolution = GameResolutionService.Set(request.width, request.height);
                    break;
                case "list-packages":
                    PackageService.List(response);
                    break;
                case "resolve-packages":
                    PackageService.Resolve(response);
                    break;
                case "search-packages":
                    PackageService.Search(response);
                    break;
                case "add-package":
                    PackageService.AddOrUpdate(
                        response,
                        request.names != null && request.names.Length > 0 ? request.names : new[] { request.name },
                        request.action);
                    break;
                case "remove-package":
                    PackageService.Remove(response, request.names != null && request.names.Length > 0 ? request.names : new[] { request.name });
                    break;
                case "list-input-axes":
                    response.axes = InputManagerService.List();
                    break;
                case "create-input-axis":
                    response.axis = InputManagerService.Create(request.name, request.values);
                    break;
                case "delete-input-axis":
                    response.message = "Axes deleted: " + InputManagerService.Delete(request.name);
                    break;
                case "list-build-scenes":
                    response.message = BuildSettingsService.List();
                    break;
                case "mutate-build-scene":
                    response.message = BuildSettingsService.Mutate(request.action, request.path, request.siblingIndex);
                    break;
                case "prepare-game-interaction":
                    response.message = GameInteractionService.Prepare();
                    break;
                case "dispatch-game-input":
                    GameInteractionService.CompleteDispatch(request);
                    break;
                case "pause-and-capture-game":
                    response.message = GameInteractionService.PauseAndCapture();
                    break;
                case "capture-game":
                    response.message = GameInteractionService.Capture(request.name, false);
                    break;
                case "game-clip-start":
                    response.message = GameClipRecorder.Start();
                    break;
                case "game-sounds-start":
                    response.message = GameSoundLog.Start();
                    break;
                case "game-sounds-stop":
                    response.message = GameSoundLog.Stop();
                    break;
                case "game-clip-stop":
                    response.message = GameClipRecorder.Stop(request.limit);
                    break;
                case "search-assets":
                    response.message = ProjectSearchService.Assets(request.query);
                    break;
                case "search-scene-references":
                    response.message = ProjectSearchService.SceneReferences(request.path);
                    break;
                case "eval-context":
                    response.message = EvalService.Context();
                    break;
                case "eval-run":
                    response.message = EvalService.Run(request.path);
                    saveAfterMutation = true;
                    break;
                case "pause-game":
                    response.message = GameInteractionService.Pause();
                    break;
                case "resume-game":
                    response.message = GameInteractionService.Resume();
                    break;
                case "step-game":
                    response.message = GameInteractionService.Step();
                    break;
                case "restore-game-time-scale":
                    response.message = GameInteractionService.RestoreTimeScale();
                    break;
                case "multiply-game-time-scale":
                    response.message = GameInteractionService.MultiplyTimeScale(request.action);
                    break;
                case "get-game-time":
                    response.message = GameInteractionService.GameTime();
                    break;
                case "pause-game-at":
                    response.message = GameInteractionService.PauseAt(request.action);
                    break;
                case "pause-game-when":
                    response.message = GameInteractionService.PauseWhen(request.action);
                    break;
                case "game-wait-status":
                    response.message = GameInteractionService.WaitStatus();
                    break;
                case "capture-scene":
                {
                    string[] screenshotLabels;
                    response.screenshots = SceneScreenshotService.Capture(request.paths, request.action, request.value, out screenshotLabels);
                    response.screenshotLabels = screenshotLabels;
                    response.message = SceneScreenshotService.Note;
                    break;
                }
                case "list-animation-clips":
                    response.message = AnimationClipService.ListClips(request.path);
                    break;
                case "get-animation-table":
                    response.message = AnimationClipService.GetTable(request.clip);
                    break;
                case "get-animation-properties":
                    response.message = AnimationClipService.GetAvailableProperties(request.path, request.boolValue);
                    break;
                case "create-animation-clip":
                    response.message = AnimationClipService.CreateClip(request.name, request.path);
                    break;
                case "delete-animation-clip":
                    response.message = AnimationClipService.DeleteClip(request.clip);
                    break;
                case "mutate-animation-events":
                    response.message = AnimationClipService.MutateEvents(request);
                    break;
                case "mutate-animation-property":
                    response.message = AnimationClipService.MutateProperty(request);
                    break;
                case "animation-clip-setting":
                    response.message = AnimationClipService.ClipSetting(request);
                    break;
                case "list-animators":
                    response.message = AnimatorControllerService.ListAnimators();
                    break;
                case "get-animator":
                    response.message = AnimatorControllerService.GetAnimator(request.path);
                    break;
                case "mutate-animator":
                    response.message = AnimatorControllerService.MutateAnimator(request);
                    saveAfterMutation = true;
                    break;
                case "assign-animator-controller":
                    response.message = AnimatorControllerService.AssignController(request);
                    saveAfterMutation = true;
                    break;
                case "get-animator-motions":
                    response.message = AnimatorControllerService.GetMotions(request);
                    break;
                case "get-animator-controller":
                    response.message = AnimatorControllerService.GetController(request);
                    break;
                case "mutate-animator-state":
                    response.message = AnimatorControllerService.MutateState(request);
                    break;
                case "assign-animator-state-motion":
                    response.message = AnimatorControllerService.AssignStateMotion(request);
                    break;
                case "mutate-animator-transition":
                    response.message = AnimatorControllerService.MutateTransition(request);
                    break;
                case "mutate-animator-parameter":
                    response.message = AnimatorControllerService.MutateParameter(request);
                    break;
                case "mutate-animator-layer":
                    response.message = AnimatorControllerService.MutateLayer(request);
                    break;
                case "mutate-animator-state-machine":
                    response.message = AnimatorControllerService.MutateStateMachine(request);
                    break;
                case "mutate-animator-blend-tree":
                    response.message = AnimatorControllerService.MutateBlendTree(request);
                    break;
                case "control-animator":
                    response.message = AnimatorRuntimeService.Control(request);
                    break;
                case "get-animator-runtime-state":
                    response.message = AnimatorRuntimeService.GetState(request.path);
                    break;
                case "list-timelines":
                case "get-timeline":
                    response.message = RequireTimeline(request);
                    break;
                case "mutate-timeline-track":
                case "mutate-timeline-clip":
                case "mutate-timeline-marker":
                    response.message = RequireTimeline(request);
                    saveAfterMutation = true;
                    break;
                case "material-preview":
                    response.message = MaterialPreviewService.Capture(request);
                    break;
                case "shader-graph-preview":
                    if (ShaderGraphPreview == null)
                        throw new InvalidOperationException("Shader Graph previews need the com.unity.shadergraph package.");
                    response.message = ShaderGraphPreview(request);
                    break;
                default:
                    throw new InvalidOperationException("Unknown or excluded command: " + request.command);
            }

            if (saveAfterMutation)
                ScenePersistenceService.SaveAfterBridgeMutation();
            // Until the reload, components keep the data layout of the old script: removed fields shift the values read.
            if (request.command == "get-object-info" && (EditorApplication.isCompiling || AssetRefreshService.CompilationPending()))
                response.note = "Scripts are compiling: component values follow the old scripts until the reload; read again after compile.";
            // eval runs any code, mostly reads: the plugin cannot tell it changed the scene.
            if (saveAfterMutation && EditorApplication.isPlaying && request.command != "eval-run")
                response.note = "Play Mode: the scene change is lost when the game stops.";

            return response;
        }

        private static string RequireTimeline(BridgeRequest request)
        {
            if (Timeline == null)
                throw new InvalidOperationException("Timeline commands need the com.unity.timeline package.");
            return Timeline(request);
        }
    }
}
