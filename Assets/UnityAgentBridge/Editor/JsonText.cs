using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityAgentBridge.Editor
{
    // Ordered JSON object for views whose keys are Unity labels (JsonUtility cannot write dictionaries).
    internal sealed class JsonText
    {
        private readonly List<KeyValuePair<string, object>> entries = new List<KeyValuePair<string, object>>();

        internal int Count { get { return entries.Count; } }

        internal IEnumerable<KeyValuePair<string, object>> Items { get { return entries; } }

        internal bool Contains(string key)
        {
            return entries.Exists(entry => entry.Key == key);
        }

        internal JsonText Add(string key, object value)
        {
            entries.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }

        internal JsonText AddIf(bool condition, string key, object value)
        {
            return condition ? Add(key, value) : this;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            Write(builder, this);
            return builder.ToString();
        }

        // JSON another command already writes, kept as it is.
        internal sealed class Raw
        {
            internal readonly string Json;

            internal Raw(string json)
            {
                Json = json;
            }
        }

        private static void Write(StringBuilder builder, object value)
        {
            if (value is Raw)
            {
                builder.Append(((Raw)value).Json);
                return;
            }
            var json = value as JsonText;
            if (json != null)
            {
                builder.Append('{');
                for (var index = 0; index < json.entries.Count; index++)
                {
                    if (index > 0)
                        builder.Append(',');
                    WriteString(builder, json.entries[index].Key);
                    builder.Append(':');
                    Write(builder, json.entries[index].Value);
                }
                builder.Append('}');
                return;
            }
            if (value == null)
                builder.Append("null");
            else if (value is string)
                WriteString(builder, (string)value);
            else if (value is bool)
                builder.Append((bool)value ? "true" : "false");
            else if (value is int || value is long || value is float || value is double)
                builder.Append(Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture));
            else if (value is IEnumerable)
            {
                builder.Append('[');
                var first = true;
                foreach (var item in (IEnumerable)value)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    Write(builder, item);
                }
                builder.Append(']');
            }
            else
                WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < ' ')
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        else
                            builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
