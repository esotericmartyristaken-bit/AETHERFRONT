using System;
using System.Collections.Generic;
using System.IO;

namespace Aetherfront.Synchronization
{
    // Slices canonical, already trusted serializer output. It is not a replacement
    // for deserializing or validating a peer's request.
    public static class JsonSlices
    {
        public static Dictionary<string, string> Object(string json)
        {
            CheckSize(json); int at = 0; White(json, ref at); Need(json, ref at, '{');
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            White(json, ref at);
            if (at < json.Length && json[at] != '}')
                for (;;)
                {
                    White(json, ref at); int start = at; StringEnd(json, ref at);
                    string key = json.Substring(start + 1, at - start - 2);
                    // State property names are fixed CLR identifiers, never escaped.
                    if (key.IndexOf('\\') >= 0) throw new InvalidDataException("Escaped canonical property name.");
                    White(json, ref at); Need(json, ref at, ':'); White(json, ref at);
                    start = at; ValueEnd(json, ref at);
                    if (values.ContainsKey(key)) throw new InvalidDataException("Duplicate canonical property.");
                    values.Add(key, json.Substring(start, at - start).TrimEnd()); White(json, ref at);
                    if (at >= json.Length || json[at] != ',') break;
                    at++;
                }
            Need(json, ref at, '}'); White(json, ref at);
            if (at != json.Length) throw new InvalidDataException("Trailing canonical object data.");
            return values;
        }
        public static List<string> Array(string json)
        {
            CheckSize(json); int at = 0; White(json, ref at); var values = new List<string>();
            if (json.Substring(at).TrimEnd() == "null") return values;
            Need(json, ref at, '['); White(json, ref at);
            if (at < json.Length && json[at] != ']')
                for (;;)
                {
                    White(json, ref at); int start = at; ValueEnd(json, ref at);
                    values.Add(json.Substring(start, at - start).TrimEnd()); White(json, ref at);
                    if (at >= json.Length || json[at] != ',') break;
                    at++;
                }
            Need(json, ref at, ']'); White(json, ref at);
            if (at != json.Length) throw new InvalidDataException("Trailing canonical array data.");
            return values;
        }
        static void CheckSize(string json)
        { if (json == null || json.Length > 64 * 1024 * 1024) throw new InvalidDataException("Canonical JSON size invalid."); }
        static void White(string json, ref int at) { while (at < json.Length && char.IsWhiteSpace(json[at])) at++; }
        static void Need(string json, ref int at, char value)
        { if (at >= json.Length || json[at++] != value) throw new InvalidDataException("Canonical JSON delimiter invalid."); }
        static void StringEnd(string json, ref int at)
        {
            Need(json, ref at, '"');
            while (at < json.Length)
            {
                char c = json[at++];
                if (c == '"') return;
                if (c == '\\') { if (at >= json.Length) break; at++; }
                else if (c < 32) throw new InvalidDataException("Canonical JSON control character.");
            }
            throw new InvalidDataException("Unterminated canonical string.");
        }
        static void ValueEnd(string json, ref int at)
        {
            int start = at, depth = 0; var stack = new char[128];
            while (at < json.Length)
            {
                char c = json[at];
                if (c == '"') { StringEnd(json, ref at); continue; }
                if (c == '{' || c == '[')
                { if (depth == stack.Length) throw new InvalidDataException("Canonical JSON nesting limit."); stack[depth++] = c; at++; continue; }
                if (c == '}' || c == ']')
                {
                    if (depth == 0) break;
                    if ((c == '}' ? '{' : '[') != stack[--depth]) throw new InvalidDataException("Canonical JSON nesting invalid.");
                    at++; continue;
                }
                if (c == ',' && depth == 0) break;
                at++;
            }
            if (depth != 0 || at == start || string.IsNullOrWhiteSpace(json.Substring(start, at - start)))
                throw new InvalidDataException("Canonical JSON value invalid.");
        }
    }
}
