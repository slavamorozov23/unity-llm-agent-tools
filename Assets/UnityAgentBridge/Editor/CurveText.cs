using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // Curves and gradients as object-info prints them, written back without JSON quoting:
    // a curve "0:0 0.5:1 1:0" (time:value), a gradient "0:#FF8000 1:#0000FF / 0:1 1:0" (colors / alphas).
    internal static class CurveText
    {
        internal static bool Is(string text)
        {
            text = text.Trim();
            return text.Length > 0 && text[0] != '{' && text[0] != '[' && text.Contains(":");
        }

        // New keys get Clamped Auto tangents, as keys added in the Curve Editor do.
        internal static AnimationCurve Curve(string text)
        {
            var keys = Tokens(text).Select(token =>
            {
                var pair = Pair(token);
                return new Keyframe(Number(pair[0]), Number(pair[1]));
            }).ToArray();
            if (keys.Length == 0)
                throw new FormatException("A curve is time:value pairs, e.g. 0:0 1:1.");
            var curve = new AnimationCurve(keys);
            for (var index = 0; index < curve.length; index++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
            }
            return curve;
        }

        internal static Gradient Gradient(string text)
        {
            var parts = text.Split('/');
            if (parts.Length > 2)
                throw new FormatException("A gradient is time:color keys, then optionally / time:alpha keys.");
            var colors = Tokens(parts[0]).Select(token =>
            {
                var pair = Pair(token);
                return new GradientColorKey(Color(pair[1]), Number(pair[0]));
            }).ToArray();
            var alphas = parts.Length == 1
                ? new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
                : Tokens(parts[1]).Select(token =>
                {
                    var pair = Pair(token);
                    return new GradientAlphaKey(Number(pair[1]), Number(pair[0]));
                }).ToArray();
            if (colors.Length == 0 || alphas.Length == 0)
                throw new FormatException("A gradient is time:color keys, then optionally / time:alpha keys.");
            var gradient = new Gradient();
            gradient.SetKeys(colors, alphas);
            return gradient;
        }

        private static string[] Tokens(string text)
        {
            return text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string[] Pair(string token)
        {
            var separator = token.IndexOf(':');
            if (separator <= 0 || separator == token.Length - 1)
                throw new FormatException("Expected time:value, got " + token);
            return new[] { token.Substring(0, separator), token.Substring(separator + 1) };
        }

        private static float Number(string text)
        {
            float value;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new FormatException("Not a number: " + text);
            return value;
        }

        // #RRGGBB as in the Color Picker's Hexadecimal field, or r,g,b.
        private static Color Color(string text)
        {
            Color color;
            if (text.StartsWith("#", StringComparison.Ordinal) && ColorUtility.TryParseHtmlString(text, out color))
                return color;
            var channels = text.Split(',');
            if (channels.Length != 3)
                throw new FormatException("A gradient color is #RRGGBB or r,g,b: " + text);
            return new Color(Number(channels[0]), Number(channels[1]), Number(channels[2]));
        }
    }
}
