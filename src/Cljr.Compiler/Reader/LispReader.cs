using System.Globalization;
using System.Text;

namespace Cljr.Compiler.Reader;

/// <summary>
/// Reads Clojure S-expressions from text input
/// </summary>
public class LispReader
{
    private readonly TextReader _reader;
    private int _line = 1;
    private int _column = 0;

    public LispReader(TextReader reader)
    {
        _reader = reader;
    }

    public LispReader(string input) : this(new StringReader(input)) { }

    /// <summary>
    /// Read all forms from the input
    /// </summary>
    public static IEnumerable<object?> ReadAll(string input)
    {
        var reader = new LispReader(input);
        while (true)
        {
            var form = reader.Read();
            if (form is EofObject) yield break;
            yield return form;
        }
    }

    /// <summary>
    /// Read a single form from the input
    /// </summary>
    public object? Read()
    {
        SkipWhitespaceAndComments();

        int ch = Peek();
        if (ch == -1) return EofObject.Instance;

        // Handle comment - skip line and recurse
        if (ch == ';')
        {
            SkipLine();
            return Read();
        }

        return ch switch
        {
            '(' => ReadList(),
            '[' => ReadVector(),
            '{' => ReadMap(),
            '"' => ReadString(),
            ':' => ReadKeyword(),
            '\'' => ReadQuote(),
            '`' => ReadSyntaxQuote(),
            '~' => ReadUnquote(),
            '@' => ReadDeref(),
            '^' => ReadMeta(),
            '#' => ReadDispatch(),
            '|' => ReadPipeEscapedSymbol(),
            '\\' => ReadCharLiteral(),
            _ => ReadAtom()
        };
    }

    #region Core Readers

    private PersistentList ReadList()
    {
        Consume('(');
        var items = ReadUntil(')');
        Consume(')');
        return new PersistentList(items);
    }

    private PersistentVector ReadVector()
    {
        Consume('[');
        var items = ReadUntil(']');
        Consume(']');
        return new PersistentVector(items);
    }

    private PersistentMap ReadMap()
    {
        Consume('{');
        var items = ReadUntil('}');
        Consume('}');

        if (items.Count % 2 != 0)
            throw new ReaderException("Map literal must have even number of forms", _line, _column);

        var pairs = new List<KeyValuePair<object, object?>>();
        for (int i = 0; i < items.Count; i += 2)
        {
            pairs.Add(new KeyValuePair<object, object?>(items[i]!, items[i + 1]));
        }
        return new PersistentMap(pairs);
    }

    private List<object?> ReadUntil(char end)
    {
        var items = new List<object?>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (Peek() == end) break;
            if (Peek() == -1)
                throw new ReaderException($"Unexpected EOF, expected '{end}'", _line, _column);
            items.Add(Read());
        }
        return items;
    }

    private string ReadString()
    {
        Consume('"');
        var sb = new StringBuilder();

        while (true)
        {
            int ch = NextChar();
            if (ch == -1)
                throw new ReaderException("Unexpected EOF in string", _line, _column);

            if (ch == '"') break;

            if (ch == '\\')
            {
                int escaped = NextChar();
                sb.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '"' => '"',
                    'u' => ReadUnicodeEscape(),
                    _ => throw new ReaderException($"Unknown escape: \\{(char)escaped}", _line, _column)
                });
            }
            else
            {
                sb.Append((char)ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads a Clojure character literal like \a, \newline, \return, \space, \tab, etc.
    /// </summary>
    private char ReadCharLiteral()
    {
        Consume('\\');

        // Read the character or name
        int firstChar = NextChar();
        if (firstChar == -1)
            throw new ReaderException("Unexpected EOF in character literal", _line, _column);

        // Check if this is a named character like \newline or just a single char like \a
        if (char.IsLetter((char)firstChar))
        {
            var sb = new StringBuilder();
            sb.Append((char)firstChar);

            // Try to read more letters for named chars
            while (Peek() != -1 && char.IsLetterOrDigit((char)Peek()))
            {
                sb.Append((char)NextChar());
            }

            var name = sb.ToString();

            // Single letter character
            if (name.Length == 1)
                return name[0];

            // Named characters
            return name switch
            {
                "newline" => '\n',
                "return" => '\r',
                "space" => ' ',
                "tab" => '\t',
                "backspace" => '\b',
                "formfeed" => '\f',
                _ => name.Length == 1 ? name[0] : throw new ReaderException($"Unknown character: \\{name}", _line, _column)
            };
        }

        // Unicode escape \uXXXX
        if (firstChar == 'u')
        {
            return ReadUnicodeEscape();
        }

        // Return the single character
        return (char)firstChar;
    }

    private char ReadUnicodeEscape()
    {
        var hex = new char[4];
        for (int i = 0; i < 4; i++)
        {
            int ch = NextChar();
            if (ch == -1)
                throw new ReaderException("Unexpected EOF in unicode escape", _line, _column);
            hex[i] = (char)ch;
        }
        return (char)int.Parse(new string(hex), NumberStyles.HexNumber);
    }

    private Keyword ReadKeyword()
    {
        Consume(':');
        var name = ReadSymbolName();
        return Keyword.Parse(name);
    }

    private object? ReadAtom()
    {
        var name = ReadSymbolName();

        // Check for nil, true, false
        if (name == "nil") return null;
        if (name == "true") return true;
        if (name == "false") return false;

        // Try to parse as number
        if (TryParseNumber(name, out var num))
            return num;

        // It's a symbol
        return Symbol.Parse(name);
    }

    /// <summary>
    /// Reads a pipe-escaped symbol like |string[]| or |Task&lt;IList&lt;User&gt;&gt;|
    /// Allows any characters inside except unescaped |
    /// Use || to escape a literal pipe character
    /// </summary>
    private Symbol ReadPipeEscapedSymbol()
    {
        int startLine = _line;
        int startColumn = _column;

        Consume('|');
        var sb = new StringBuilder();

        while (true)
        {
            int ch = NextChar();

            if (ch == -1)
                throw new ReaderException("Unexpected EOF in pipe-escaped symbol", startLine, startColumn);

            if (ch == '|')
            {
                if (Peek() == '|')  // || escapes literal pipe
                {
                    NextChar();
                    sb.Append('|');
                }
                else
                    break;  // End of symbol
            }
            else
                sb.Append((char)ch);
        }

        if (sb.Length == 0)
            throw new ReaderException("Empty pipe-escaped symbol ||", startLine, startColumn);

        return Symbol.Parse(sb.ToString());
    }

    private string ReadSymbolName()
    {
        var sb = new StringBuilder();

        while (true)
        {
            int ch = Peek();
            if (ch == -1 || IsTerminator((char)ch))
                break;

            // Handle embedded pipe-escaped sequences (e.g., Foo/|Bar<T, U>|)
            if (ch == '|')
            {
                sb.Append((char)NextChar()); // consume opening |
                while (true)
                {
                    int pch = Peek();
                    if (pch == -1)
                        throw new ReaderException("Unexpected EOF in pipe-escaped symbol", _line, _column);
                    if (pch == '|')
                    {
                        sb.Append((char)NextChar()); // consume closing |
                        if (Peek() == '|')  // || escapes literal pipe
                        {
                            sb.Append((char)NextChar());
                        }
                        else
                            break; // End of pipe-escaped section
                    }
                    else
                    {
                        sb.Append((char)NextChar());
                    }
                }
            }
            else
            {
                sb.Append((char)NextChar());
            }
        }

        if (sb.Length == 0)
            throw new ReaderException("Expected symbol", _line, _column);

        return sb.ToString();
    }

    private static bool IsTerminator(char ch) =>
        char.IsWhiteSpace(ch) || ch is '(' or ')' or '[' or ']' or '{' or '}' or '"' or ';' or ',';

    private static bool TryParseNumber(string s, out object? result)
    {
        result = null;

        // Try integer
        if (long.TryParse(s, out var l))
        {
            result = l is >= int.MinValue and <= int.MaxValue ? (int)l : l;
            return true;
        }

        // Try hex
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s[2..], NumberStyles.HexNumber, null, out var hex))
        {
            result = hex is >= int.MinValue and <= int.MaxValue ? (int)hex : hex;
            return true;
        }

        // Try double
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            result = d;
            return true;
        }

        // Try ratio (e.g., 1/2)
        if (s.IndexOf('/') >= 0 && !s.StartsWith("/", StringComparison.Ordinal))
        {
            var parts = s.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var num) &&
                double.TryParse(parts[1], out var denom) &&
                denom != 0)
            {
                result = num / denom;
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Quote/Unquote Readers

    private PersistentList ReadQuote()
    {
        Consume('\'');
        return new PersistentList([Symbol.Parse("quote"), Read()]);
    }

    private PersistentList ReadSyntaxQuote()
    {
        Consume('`');
        return new PersistentList([Symbol.Parse("syntax-quote"), Read()]);
    }

    private object? ReadUnquote()
    {
        Consume('~');
        if (Peek() == '@')
        {
            Consume('@');
            return new PersistentList([Symbol.Parse("unquote-splicing"), Read()]);
        }
        return new PersistentList([Symbol.Parse("unquote"), Read()]);
    }

    private PersistentList ReadDeref()
    {
        Consume('@');
        return new PersistentList([Symbol.Parse("deref"), Read()]);
    }

    #endregion

    #region Metadata Reader

    private object? ReadMeta()
    {
        Consume('^');

        // Read the metadata
        var meta = Read();

        // Metadata can be:
        // ^:keyword -> {:keyword true}
        // ^Type -> {:tag Type}
        // ^"string[]" -> {:tag "string[]"} (for array types)
        // ^{...} -> the map itself

        Dictionary<object, object> metaMap;

        if (meta is Keyword kw)
        {
            metaMap = new Dictionary<object, object> { [kw] = true };
        }
        else if (meta is Symbol sym)
        {
            metaMap = new Dictionary<object, object> { [Keyword.Intern("tag")] = sym };
        }
        else if (meta is string str)
        {
            // Support string type hints for array types like "string[]" that can't be symbols
            metaMap = new Dictionary<object, object> { [Keyword.Intern("tag")] = str };
        }
        else if (meta is PersistentMap pm)
        {
            metaMap = pm.ToDictionary(kv => kv.Key, kv => kv.Value!);
        }
        else
        {
            throw new ReaderException($"Metadata must be keyword, symbol, string, or map, got {meta?.GetType().Name}", _line, _column);
        }

        // Read the target form
        var target = Read();

        // Attach metadata - merge with existing meta if present
        // This allows stacked metadata like ^{:attr [Param]} ^int foo
        return target switch
        {
            Symbol s => s.WithMeta(MergeMetadata(s.Meta, metaMap!)),
            PersistentList l => l.WithMeta(MergeMetadata(l.Meta, metaMap!)),
            PersistentVector v => v.WithMeta(MergeMetadata(v.Meta, metaMap!)),
            PersistentMap m => m.WithMeta(MergeMetadata(m.Meta, metaMap!)),
            _ => throw new ReaderException($"Cannot attach metadata to {target?.GetType().Name}", _line, _column)
        };
    }

    /// <summary>
    /// Merge existing metadata with new metadata. New values take precedence.
    /// </summary>
    private static Dictionary<object, object> MergeMetadata(
        IReadOnlyDictionary<object, object>? existing,
        Dictionary<object, object> newMeta)
    {
        if (existing is null || existing.Count == 0)
            return newMeta;

        // Copy existing metadata and overlay new metadata
        // Note: Can't use Dictionary(IReadOnlyDictionary) constructor in netstandard2.0
        var merged = new Dictionary<object, object>();
        foreach (var kv in existing)
        {
            merged[kv.Key] = kv.Value;
        }
        foreach (var kv in newMeta)
        {
            merged[kv.Key] = kv.Value;
        }
        return merged;
    }

    #endregion

    #region Dispatch Reader (#)

    private object? ReadDispatch()
    {
        Consume('#');
        int ch = Peek();

        return ch switch
        {
            '{' => ReadSet(),
            '(' => ReadAnonFn(),
            '\'' => ReadVar(),
            '_' => ReadDiscard(),
            '"' => ReadRegex(),
            ':' => ReadNamespacedMap(),
            _ => throw new ReaderException($"Unknown dispatch: #{(char)ch}", _line, _column)
        };
    }

    private PersistentSet ReadSet()
    {
        Consume('{');
        var items = ReadUntil('}');
        Consume('}');
        return new PersistentSet(items);
    }

    private PersistentList ReadAnonFn()
    {
        // #(+ % 1) -> (fn* [p1__auto__] (+ p1__auto__ 1))
        // Parse the body and replace % with a valid C# identifier
        Consume('(');
        var body = ReadUntil(')');
        Consume(')');

        // Replace % symbols with valid C# identifiers in the body
        var paramName = Symbol.Parse("p1__auto__");
        var percentSymbol = Symbol.Parse("%");
        var transformedBody = ReplaceSymbolInList(body, percentSymbol, paramName);

        return new PersistentList([Symbol.Parse("fn*"), new PersistentVector([paramName]), new PersistentList(transformedBody)]);
    }

    private static List<object?> ReplaceSymbolInList(List<object?> items, Symbol from, Symbol to)
    {
        var result = new List<object?>();
        foreach (var item in items)
        {
            result.Add(ReplaceSymbolInForm(item, from, to));
        }
        return result;
    }

    private static object? ReplaceSymbolInForm(object? form, Symbol from, Symbol to)
    {
        return form switch
        {
            Symbol s when s.Name == from.Name && s.Namespace == from.Namespace => to,
            PersistentList list => new PersistentList(ReplaceSymbolInList(list.ToList(), from, to)),
            PersistentVector vec => new PersistentVector(ReplaceSymbolInList(vec.ToList(), from, to)),
            PersistentSet set => new PersistentSet(ReplaceSymbolInList(set.ToList(), from, to)),
            // Hash maps in #() bodies are rare - just pass through unchanged
            _ => form
        };
    }

    private PersistentList ReadVar()
    {
        Consume('\'');
        return new PersistentList([Symbol.Parse("var"), Read()]);
    }

    private object? ReadDiscard()
    {
        Consume('_');
        Read(); // Read and discard the next form
        return Read(); // Return the form after that
    }

    private object ReadRegex()
    {
        var pattern = ReadString();
        // Return as a tagged form - the analyzer/emitter will handle it
        return new PersistentList([Symbol.Parse("re-pattern"), pattern]);
    }

    private object? ReadNamespacedMap()
    {
        Consume(':');
        // #::{:a 1} or #:foo{:a 1}
        // For now, just read the namespace and map
        var ns = Peek() == '{' ? null : ReadSymbolName();
        SkipWhitespaceAndComments();
        var map = ReadMap();
        // TODO: Resolve namespaced keys
        return map;
    }

    #endregion

    #region Low-level Char Handling

    private int Peek() => _reader.Peek();

    private int NextChar()
    {
        int ch = _reader.Read();
        if (ch == '\n')
        {
            _line++;
            _column = 0;
        }
        else
        {
            _column++;
        }
        return ch;
    }

    private void Consume(char expected)
    {
        int ch = NextChar();
        if (ch != expected)
            throw new ReaderException($"Expected '{expected}', got '{(char)ch}'", _line, _column);
    }

    private void SkipWhitespaceAndComments()
    {
        while (true)
        {
            int ch = Peek();
            if (ch == -1) return;

            if (char.IsWhiteSpace((char)ch) || ch == ',')
            {
                NextChar();
                continue;
            }

            if (ch == ';')
            {
                SkipLine();
                continue;
            }

            break;
        }
    }

    private void SkipLine()
    {
        while (true)
        {
            int ch = NextChar();
            if (ch == -1 || ch == '\n') break;
        }
    }

    #endregion
}

/// <summary>
/// Sentinel value for end of file
/// </summary>
public sealed class EofObject
{
    public static readonly EofObject Instance = new();
    private EofObject() { }
    public override string ToString() => "#<eof>";
}

/// <summary>
/// Exception thrown during reading
/// </summary>
public class ReaderException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public ReaderException(string message, int line, int column)
        : base($"{message} at line {line}, column {column}")
    {
        Line = line;
        Column = column;
    }
}
