using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// A surgical edit of a JSON text (comments and trailing commas allowed, as in the user config):
/// only the value spans of the changed keys are replaced, a missing key is inserted into its
/// nearest existing parent object, and every other byte — comments, formatting, key order,
/// unknown keys — stays as written. Keys are matched case-insensitively (the config loads them
/// that way); of duplicate keys the last one is edited (the one the loader keeps). Throws
/// <see cref="JsonException"/> / <see cref="InvalidDataException"/> when the text cannot be
/// edited this way (a malformed document, a non-object where the path needs an object).
/// </summary>
internal static class JsonTextPatch
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions IndentedValue = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactValue = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One edit: the key path from the root and the new value (null — the JSON literal null).</summary>
    public sealed record Edit(IReadOnlyList<string> Path, JsonNode? Value);

    public static string Apply(string text, IReadOnlyList<Edit> edits)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var edit in edits)
        {
            bytes = ApplyOne(bytes, edit, newline);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ApplyOne(byte[] bytes, Edit edit, string newline)
    {
        if (edit.Path.Count == 0)
        {
            throw new InvalidDataException("an edit needs a key path");
        }

        var node = ParseTree(bytes);
        for (var depth = 0; depth < edit.Path.Count; depth++)
        {
            var key = edit.Path[depth];
            var member = node.Members.LastOrDefault(m => string.Equals(m.Name, key, StringComparison.OrdinalIgnoreCase));
            if (member is null)
            {
                // insert the rest of the path as one new member of this object
                JsonNode? value = edit.Value?.DeepClone();
                for (var i = edit.Path.Count - 1; i > depth; i--)
                {
                    value = new JsonObject { [edit.Path[i]] = value };
                }

                return Insert(bytes, node, key, value, newline);
            }

            if (depth == edit.Path.Count - 1)
            {
                var indent = LineIndent(bytes, member.NameStart);
                var serialized = Serialize(edit.Value, indented: true, indent, newline);
                return Splice(bytes, member.ValueStart, member.ValueEnd, serialized);
            }

            node = member.Container is { IsObject: true } container
                ? container
                : throw new InvalidDataException($"'{string.Join('.', edit.Path.Take(depth + 1))}' is not an object");
        }

        throw new InvalidDataException("unreachable");
    }

    private static byte[] Insert(byte[] bytes, Container target, string key, JsonNode? value, string newline)
    {
        var name = JsonSerializer.Serialize(key, CompactValue);
        if (target.Members.Count > 0)
        {
            var last = target.Members[^1];
            if (!ContainsNewline(bytes, target.Start, last.ValueEnd))
            {
                // an inline object stays inline
                return Splice(bytes, last.ValueEnd, last.ValueEnd, $", {name}: {Serialize(value, indented: false, "", newline)}");
            }

            var indent = LineIndent(bytes, last.NameStart);
            return Splice(bytes, last.ValueEnd, last.ValueEnd, $",{newline}{indent}{name}: {Serialize(value, indented: true, indent, newline)}");
        }

        var open = target.Start + 1;
        if (!ContainsNewline(bytes, target.Start, target.End))
        {
            return Splice(bytes, open, open, $" {name}: {Serialize(value, indented: false, "", newline)}");
        }

        var childIndent = LineIndent(bytes, target.Start) + "  ";
        return Splice(bytes, open, open, $"{newline}{childIndent}{name}: {Serialize(value, indented: true, childIndent, newline)}");
    }

    private static string Serialize(JsonNode? value, bool indented, string indent, string newline)
    {
        if (value is null)
        {
            return "null";
        }

        var json = value.ToJsonString(indented ? IndentedValue : CompactValue);
        return indented
            ? json.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline + indent, StringComparison.Ordinal)
            : json;
    }

    private static bool ContainsNewline(byte[] bytes, long from, long to)
    {
        for (var i = from; i < to && i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The leading whitespace of the line that holds the position.</summary>
    private static string LineIndent(byte[] bytes, long position)
    {
        var start = position;
        while (start > 0 && bytes[start - 1] != (byte)'\n')
        {
            start--;
        }

        var end = start;
        while (end < bytes.Length && (bytes[end] == (byte)' ' || bytes[end] == (byte)'\t'))
        {
            end++;
        }

        return Encoding.UTF8.GetString(bytes, (int)start, (int)(end - start));
    }

    private static byte[] Splice(byte[] bytes, long start, long end, string replacement)
    {
        var insert = Encoding.UTF8.GetBytes(replacement);
        var result = new byte[bytes.Length - (end - start) + insert.Length];
        Array.Copy(bytes, 0, result, 0, start);
        Array.Copy(insert, 0, result, start, insert.Length);
        Array.Copy(bytes, end, result, start + insert.Length, bytes.Length - end);
        return result;
    }

    private sealed class Container
    {
        public bool IsObject { get; init; }

        public long Start { get; init; }

        public long End { get; set; }

        public List<Member> Members { get; } = [];
    }

    private sealed record Member(string Name, long NameStart, long ValueStart, long ValueEnd, Container? Container);

    /// <summary>The positions of every object member of the document, by byte offsets.</summary>
    private static Container ParseTree(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("the document is not a JSON object");
        }

        var root = ReadContainer(ref reader);
        if (reader.Read())
        {
            throw new InvalidDataException("trailing content after the root object");
        }

        return root;
    }

    private static Container ReadContainer(ref Utf8JsonReader reader)
    {
        var container = new Container { IsObject = reader.TokenType == JsonTokenType.StartObject, Start = reader.TokenStartIndex };
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                container.End = reader.TokenStartIndex;
                return container;
            }

            if (container.IsObject)
            {
                var name = reader.GetString() ?? "";
                var nameStart = reader.TokenStartIndex;
                if (!reader.Read())
                {
                    break;
                }

                var (valueStart, valueEnd, child) = ReadValue(ref reader);
                container.Members.Add(new Member(name, nameStart, valueStart, valueEnd, child));
            }
            else
            {
                ReadValue(ref reader);
            }
        }

        throw new InvalidDataException("the document ends inside an object or array");
    }

    private static (long Start, long End, Container? Child) ReadValue(ref Utf8JsonReader reader)
    {
        var start = reader.TokenStartIndex;
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            var child = ReadContainer(ref reader);
            return (start, reader.BytesConsumed, child);
        }

        return (start, reader.BytesConsumed, null);
    }
}
