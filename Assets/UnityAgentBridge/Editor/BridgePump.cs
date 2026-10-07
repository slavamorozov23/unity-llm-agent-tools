using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal sealed class BridgeNotReadyException : Exception
    {
        public BridgeNotReadyException(string message) : base(message) { }
    }

    [InitializeOnLoad]
    internal static class BridgePump
    {
        private const double SettleDelaySeconds = 0.5d;
        private static double readyAfter;
        private static bool wasEditorBusy;

        static BridgePump()
        {
            BridgePaths.EnsureRuntimeDirectories();
            readyAfter = EditorApplication.timeSinceStartup + SettleDelaySeconds;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += ProcessRequests;
        }

        private static void ProcessRequests()
        {
            if (!EditorIsReady())
                return;

            var requestFiles = Directory.GetFiles(BridgePaths.Requests, "*.request.json")
                .OrderBy(File.GetCreationTimeUtc)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();

            if (requestFiles.Length > 0)
                ProcessOne(requestFiles[0]);
        }

        private static bool ProcessOne(string requestFile)
        {
            var cancellationFile = requestFile + ".cancel";
            if (File.Exists(cancellationFile))
            {
                File.Delete(requestFile);
                File.Delete(cancellationFile);
                return true;
            }

            BridgeRequest request = null;
            BridgeResponse response;
            try
            {
                request = JsonUtility.FromJson<BridgeRequest>(File.ReadAllText(requestFile));
                if (!CommandProcessor.CanExecute(request))
                    return false;
                response = CommandProcessor.Execute(request);
            }
            catch (BridgeNotReadyException)
            {
                return false;
            }
            catch (Exception exception)
            {
                exception = Unwrap(exception);
                response = new BridgeResponse
                {
                    id = request == null ? Path.GetFileName(requestFile).Replace(".request.json", string.Empty) : request.id,
                    ok = false,
                    error = exception is EvalService.EvalFailure ? exception.Message : exception.GetType().Name + ": " + exception.Message
                };
            }

            try
            {
                if (File.Exists(cancellationFile))
                    return true;
                var safeId = SafeId(response.id);
                var responsePath = Path.Combine(BridgePaths.Responses, safeId + ".response.json");
                var temporaryPath = responsePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonUtility.ToJson(response, false));
                if (File.Exists(responsePath))
                    File.Delete(responsePath);
                File.Move(temporaryPath, responsePath);
            }
            finally
            {
                if (File.Exists(requestFile))
                    File.Delete(requestFile);
                if (File.Exists(cancellationFile))
                    File.Delete(cancellationFile);
            }
            return true;
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException && exception.InnerException != null)
                exception = exception.InnerException;
            return exception;
        }

        private static bool EditorIsReady()
        {
            var busy = EditorApplication.isCompiling || EditorApplication.isUpdating;
            if (busy)
            {
                wasEditorBusy = true;
                return false;
            }
            if (wasEditorBusy)
            {
                wasEditorBusy = false;
                readyAfter = EditorApplication.timeSinceStartup + SettleDelaySeconds;
            }
            return EditorApplication.timeSinceStartup >= readyAfter;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            readyAfter = EditorApplication.timeSinceStartup + SettleDelaySeconds;
        }

        private static string SafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
                throw new InvalidOperationException("Request id may contain only letters, digits, and '-'.");
            return id;
        }
    }
}
