using System.Text;

namespace OverlayUtils.Core.Games;

/// <summary>A node of Valve's KeyValues format: either a value or a block of named children (keys ignore case).</summary>
public sealed class VdfNode
{
    readonly Dictionary<string, VdfNode> _children = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode(string? value = null) => Text = value;

    /// <summary>The value of a leaf; null for a block.</summary>
    public string? Text { get; }

    public IEnumerable<KeyValuePair<string, VdfNode>> Children => _children;

    public VdfNode? Get(string key) => _children.GetValueOrDefault(key);

    /// <summary>The value of a child leaf, or null.</summary>
    public string? Value(string key) => Get(key)?.Text;

    internal void Add(string key, VdfNode node) => _children.TryAdd(key, node); // first occurrence wins, as in Python
}

/// <summary>Parsers for Steam's text VDF (manifests, libraryfolders) and binary VDF (shortcuts.vdf).</summary>
public static class Vdf
{
    /// <summary>Parses text KeyValues. Malformed input yields what was read so far.</summary>
    public static VdfNode ParseText(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        string? pendingKey = null;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{')
            {
                i++;
                if (pendingKey is null) continue;
                var block = new VdfNode();
                stack.Peek().Add(pendingKey, block);
                stack.Push(block);
                pendingKey = null;
                continue;
            }
            if (c == '}')
            {
                i++;
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
                continue;
            }
            var token = c == '"' ? ReadQuoted(text, ref i) : ReadBare(text, ref i);
            if (pendingKey is null) pendingKey = token;
            else
            {
                stack.Peek().Add(pendingKey, new VdfNode(token));
                pendingKey = null;
            }
        }
        return root;
    }

    static string ReadQuoted(string text, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
            }
            else sb.Append(text[i]);
            i++;
        }
        i++; // closing quote
        return sb.ToString();
    }

    static string ReadBare(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
        return text[start..i];
    }

    /// <summary>
    /// Parses binary KeyValues (0x00 block, 0x01 string, 0x02 int32, 0x08 end; keys lower-cased as Python).
    /// Throws <see cref="FormatException"/> on truncated or unknown data.
    /// </summary>
    public static VdfNode ParseBinary(byte[] data)
    {
        var pos = 0;
        return ReadMap(data, ref pos);
    }

    static VdfNode ReadMap(byte[] data, ref int pos)
    {
        var node = new VdfNode();
        while (pos < data.Length)
        {
            var kind = data[pos++];
            if (kind == 0x08) return node;
            var key = ReadCString(data, ref pos).ToLowerInvariant();
            switch (kind)
            {
                case 0x00:
                    node.Add(key, ReadMap(data, ref pos));
                    break;
                case 0x01:
                    node.Add(key, new VdfNode(ReadCString(data, ref pos)));
                    break;
                case 0x02:
                    if (pos + 4 > data.Length) throw new FormatException("truncated int32");
                    node.Add(key, new VdfNode(BitConverter.ToInt32(data, pos).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    pos += 4;
                    break;
                default:
                    throw new FormatException($"unknown binary VDF type {kind}");
            }
        }
        throw new FormatException("truncated binary VDF");
    }

    static string ReadCString(byte[] data, ref int pos)
    {
        var end = Array.IndexOf(data, (byte)0, pos);
        if (end < 0) throw new FormatException("unterminated string");
        var s = Encoding.UTF8.GetString(data, pos, end - pos);
        pos = end + 1;
        return s;
    }
}
