using System;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    [Serializable]
    internal sealed class BridgeRequest
    {
        public string id;
        public string command;
        public string path;
        public string name;
        public string[] names = Array.Empty<string>();
        public string destinationPath;
        public string templateName;
        public string componentType;
        public int componentIndex = -1;
        public int siblingIndex = -1;
        public string propertyPath;
        public string section;
        public string query;
        public string action;
        public string clip;
        public string objectPath;
        public string controller;
        public string timeline;
        public string track;
        public string layer;
        public string stateMachine;
        public string state;
        public string fromState;
        public string toState;
        public string motion;
        public string parameterType;
        public string value;
        public string json;
        public bool boolValue;
        public bool runtime;
        public int width;
        public int height;
        public int limit = 10;
        public PropertyValue[] values = Array.Empty<PropertyValue>();
        public string[] paths = Array.Empty<string>();
    }

    [Serializable]
    internal sealed class PropertyValue
    {
        public string path;
        public string value;
        public bool append;
        public bool remove;
    }

    [Serializable]
    internal sealed class BridgeResponse
    {
        public string id;
        public bool ok;
        public string message;
        public string error;
        public SceneObjectData[] objects;
        public SceneObjectData objectInfo;
        public string[] componentTypes;
        public CandidateData[] candidates;
        public PrefabData[] prefabs;
        public SceneAssetData[] scenes;
        public CreationTemplateData[] templates;
        public string[] objectTemplates;
        public string[] items;
        public AssetData assetInfo;
        public LogData[] logs;
        public LogData[] currentCompilationErrors;
        public GameResolutionData[] resolutions;
        public GameResolutionData resolution;
        public PackageData[] packages;
        public PackageData package;
        public PackageChangeData[] packageChanges;
        public InputAxisData[] axes;
        public InputAxisData axis;
        public string[] screenshots;
        public string[] screenshotLabels;
        public bool pending;
        public string status;
        public string note;
        public int componentIndex = -1;
    }

    [Serializable]
    internal sealed class GameResolutionData
    {
        public int width;
        public int height;
    }

    [Serializable]
    internal sealed class PackageData
    {
        public string name;
        public string displayName;
        public string version;
        public string description;
        public string source;
        public bool direct;
        public PackageDependencyData[] dependencies;
        public string minimumUnity;
        public bool compatible;
    }

    [Serializable]
    internal sealed class PackageChangeData
    {
        public string name;
        public string previousVersion;
        public string version;
    }

    [Serializable]
    internal sealed class PackageDependencyData
    {
        public string name;
        public string version;
    }

    [Serializable]
    internal sealed class InputAxisData
    {
        public string name;
        public string descriptiveName;
        public string descriptiveNegativeName;
        public string negativeButton;
        public string positiveButton;
        public string altNegativeButton;
        public string altPositiveButton;
        public float gravity;
        public float dead;
        public float sensitivity;
        public bool snap;
        public bool invert;
        public int type;
        public int axis;
        public int joyNum;
    }

    [Serializable]
    internal sealed class SceneObjectData
    {
        public string path;
        public string parentPath;
        public string name;
        public string scene;
        public int depth;
        public bool activeSelf;
        public bool activeInHierarchy;
        public bool visual;
        public string tag;
        public int layer;
        public string staticFlags;
        public Vector3 worldPosition;
        public string prefabAssetPath;
        public string prefabInstanceRootPath;
        public ComponentData[] components;
    }

    [Serializable]
    internal sealed class ComponentData
    {
        public string type;
        public string assemblyQualifiedType;
        public string json;
        public string[] hidden;
        public SerializedPropertyData[] references;
        public SerializedPropertyData[] enums;
        public InspectorWarningData[] warnings;
        public InspectorActionData[] actions;
    }

    [Serializable]
    internal sealed class ModelInfoData
    {
        public string animationType;
        public string avatarSetup;
        public bool importAnimation;
        public MeshInfoData[] meshes;
    }

    [Serializable]
    internal sealed class MeshInfoData
    {
        public string name;
        public int vertexCount;
        public int subMeshCount;
        public float[] boundsCenter;
        public float[] boundsSize;
    }

    [Serializable]
    internal sealed class InspectorWarningData
    {
        public string severity;
        public string message;
    }

    [Serializable]
    internal sealed class InspectorActionData
    {
        public string id;
        public string label;
    }

    [Serializable]
    internal sealed class CandidateData
    {
        public string label;
        public string path;
        public string type;
        public string source;
    }

    [Serializable]
    internal sealed class PrefabData
    {
        public string name;
        public string assetPath;
        public string rootPath;
        public string basePrefabPath;
    }

    [Serializable]
    internal sealed class SceneAssetData
    {
        public string name;
        public string assetPath;
    }

    [Serializable]
    internal sealed class CreationTemplateData
    {
        public string name;
        public string extension;
        public string keywords;
    }

    [Serializable]
    internal sealed class AssetData
    {
        public string name;
        public string assetPath;
        public string type;
        public string importerType;
        public SerializedPropertyData[] properties;
        public InspectorActionData[] actions;
        public ModelInfoData model;
        public string view;
        public string[] subAssets;
        public string[] changes;
    }

    [Serializable]
    internal sealed class SerializedPropertyData
    {
        public string path;
        public string type;
        public string value;
        public bool writable;
    }

    [Serializable]
    internal sealed class LogData
    {
        public string timestampUtc;
        public string type;
        public string message;
        public string stackTrace;
    }

    [Serializable]
    internal sealed class LogFileData
    {
        public LogData[] entries = Array.Empty<LogData>();
    }
}
