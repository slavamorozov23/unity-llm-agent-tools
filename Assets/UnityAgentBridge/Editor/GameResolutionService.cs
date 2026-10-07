using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class GameResolutionService
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        private static readonly Assembly EditorAssembly = typeof(EditorWindow).Assembly;
        private static readonly Type GameViewType = EditorAssembly.GetType("UnityEditor.GameView", true);
        private static readonly Type GameViewSizesType = EditorAssembly.GetType("UnityEditor.GameViewSizes", true);
        private static readonly Type GameViewSizeGroupType = EditorAssembly.GetType("UnityEditor.GameViewSizeGroup", true);
        private static readonly Type GameViewSizeType = EditorAssembly.GetType("UnityEditor.GameViewSize", true);
        private static readonly PropertyInfo SizesInstance = RequiredProperty(GameViewSizesType, "instance", StaticMembers);
        private static readonly PropertyInfo CurrentGroup = RequiredProperty(GameViewSizesType, "currentGroup", InstanceMembers);
        private static readonly MethodInfo GetBuiltinCount = RequiredMethod(GameViewSizeGroupType, "GetBuiltinCount", Type.EmptyTypes);
        private static readonly MethodInfo GetCustomCount = RequiredMethod(GameViewSizeGroupType, "GetCustomCount", Type.EmptyTypes);
        private static readonly MethodInfo GetGameViewSize = RequiredMethod(GameViewSizeGroupType, "GetGameViewSize", new[] { typeof(int) });
        private static readonly PropertyInfo SizeType = RequiredProperty(GameViewSizeType, "sizeType", InstanceMembers);
        private static readonly PropertyInfo Width = RequiredProperty(GameViewSizeType, "width", InstanceMembers);
        private static readonly PropertyInfo Height = RequiredProperty(GameViewSizeType, "height", InstanceMembers);
        private static readonly MethodInfo SelectSize = RequiredMethod(GameViewType, "SizeSelectionCallback", new[] { typeof(int), typeof(object) });
        private static readonly MethodInfo GetMainGameViewSize = RequiredMethod(GameViewType, "GetSizeOfMainGameView", Type.EmptyTypes);

        public static GameResolutionData[] List()
        {
            var options = Options();
            var result = new GameResolutionData[options.Count];
            for (var i = 0; i < options.Count; i++)
                result[i] = new GameResolutionData { width = options[i].Width, height = options[i].Height };
            return result;
        }

        public static GameResolutionData Set(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Game resolution width and height must be positive integers.");

            ResolutionOption selected = null;
            foreach (var option in Options())
            {
                if (option.Width == width && option.Height == height)
                {
                    selected = option;
                    break;
                }
            }
            if (selected == null)
                throw new InvalidOperationException("Game resolution is not offered by the current Game View: " + width + "x" + height);

            var gameView = EditorWindow.GetWindow(GameViewType, false, "Game", false);
            SelectSize.Invoke(gameView, new object[] { selected.Index, null });
            gameView.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
            return new GameResolutionData { width = width, height = height };
        }

        // The size chosen in the Game View's resolution popup; null for Free Aspect and aspect ratios.
        public static GameResolutionData Selected()
        {
            var gameView = Resources.FindObjectsOfTypeAll(GameViewType).FirstOrDefault();
            var index = gameView == null ? null : GameViewType.GetProperty("selectedSizeIndex", InstanceMembers)?.GetValue(gameView);
            var option = index == null ? null : Options().FirstOrDefault(item => item.Index == (int)index);
            return option == null ? null : new GameResolutionData { width = option.Width, height = option.Height };
        }

        public static GameResolutionData Current()
        {
            var size = (Vector2)GetMainGameViewSize.Invoke(null, null);
            var width = Mathf.RoundToInt(size.x);
            var height = Mathf.RoundToInt(size.y);
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Current Game View resolution is unavailable.");
            return new GameResolutionData { width = width, height = height };
        }

        private static List<ResolutionOption> Options()
        {
            var sizes = SizesInstance.GetValue(null, null);
            var group = CurrentGroup.GetValue(sizes, null);
            var count = (int)GetBuiltinCount.Invoke(group, null) + (int)GetCustomCount.Invoke(group, null);
            var result = new List<ResolutionOption>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var size = GetGameViewSize.Invoke(group, new object[] { index });
                if (!string.Equals(SizeType.GetValue(size, null).ToString(), "FixedResolution", StringComparison.Ordinal))
                    continue;
                var width = (int)Width.GetValue(size, null);
                var height = (int)Height.GetValue(size, null);
                if (width <= 0 || height <= 0 || !seen.Add(width + "x" + height))
                    continue;
                result.Add(new ResolutionOption(index, width, height));
            }
            return result;
        }

        private static PropertyInfo RequiredProperty(Type type, string name, BindingFlags flags)
        {
            var property = type.GetProperty(name, flags);
            if (property == null)
                throw new MissingMemberException(type.FullName, name);
            return property;
        }

        private static MethodInfo RequiredMethod(Type type, string name, Type[] parameters)
        {
            var method = type.GetMethod(name, InstanceMembers | StaticMembers, null, parameters, null);
            if (method == null)
                throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private sealed class ResolutionOption
        {
            public readonly int Index;
            public readonly int Width;
            public readonly int Height;

            public ResolutionOption(int index, int width, int height)
            {
                Index = index;
                Width = width;
                Height = height;
            }
        }
    }
}
