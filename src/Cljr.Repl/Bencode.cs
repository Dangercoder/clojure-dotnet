using System.Text;

namespace Cljr.Repl;

/// <summary>
/// Bencode encoder/decoder for nREPL protocol
/// </summary>
public static class Bencode
{
    #region Encoding

    public static byte[] Encode(object? value)
    {
        using var ms = new MemoryStream();
        Encode(value, ms);
        return ms.ToArray();
    }

    public static void Encode(object? value, Stream stream)
    {
        switch (value)
        {
            case null:
                EncodeString("nil", stream);
                break;
            case string s:
                EncodeString(s, stream);
                break;
            case int i:
                EncodeInt(i, stream);
                break;
            case long l:
                EncodeInt(l, stream);
                break;
            case IList<object?> list:
                EncodeList(list, stream);
                break;
            case IDictionary<string, object?> dict:
                EncodeDict(dict, stream);
                break;
            default:
                EncodeString(value.ToString() ?? "", stream);
                break;
        }
    }

    private static void EncodeString(string s, Stream stream)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var prefix = Encoding.ASCII.GetBytes($"{bytes.Length}:");
        stream.Write(prefix);
        stream.Write(bytes);
    }

    private static void EncodeInt(long i, Stream stream)
    {
        var bytes = Encoding.ASCII.GetBytes($"i{i}e");
        stream.Write(bytes);
    }

    private static void EncodeList(IList<object?> list, Stream stream)
    {
        stream.WriteByte((byte)'l');
        foreach (var item in list)
        {
            Encode(item, stream);
        }
        stream.WriteByte((byte)'e');
    }

    private static void EncodeDict(IDictionary<string, object?> dict, Stream stream)
    {
        stream.WriteByte((byte)'d');
        foreach (var (key, value) in dict.OrderBy(kv => kv.Key))
        {
            EncodeString(key, stream);
            Encode(value, stream);
        }
        stream.WriteByte((byte)'e');
    }

    #endregion

    #region Decoding

    public static object? Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return Decode(ms);
    }

    public static object? Decode(Stream stream)
    {
        int b = stream.ReadByte();
        if (b == -1) return null;

        return (char)b switch
        {
            'i' => DecodeInt(stream),
            'l' => DecodeList(stream),
            'd' => DecodeDict(stream),
            var c when char.IsDigit(c) => DecodeString(c, stream),
            _ => throw new FormatException($"Invalid bencode: unexpected '{(char)b}'")
        };
    }

    private static long DecodeInt(Stream stream)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = stream.ReadByte()) != 'e' && b != -1)
        {
            sb.Append((char)b);
        }
        return long.Parse(sb.ToString());
    }

    private static string DecodeString(char firstDigit, Stream stream)
    {
        var lengthStr = new StringBuilder();
        lengthStr.Append(firstDigit);

        int b;
        while ((b = stream.ReadByte()) != ':' && b != -1)
        {
            lengthStr.Append((char)b);
        }

        int length = int.Parse(lengthStr.ToString());
        var buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(buffer, read, length - read);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private static List<object?> DecodeList(Stream stream)
    {
        var list = new List<object?>();
        while (true)
        {
            int peek = stream.ReadByte();
            if (peek == 'e' || peek == -1) break;

            // Put back the byte and decode
            var ms = new MemoryStream();
            ms.WriteByte((byte)peek);

            // Read the rest of the item
            var item = (char)peek switch
            {
                'i' => (object?)DecodeInt(stream),
                'l' => DecodeList(stream),
                'd' => DecodeDict(stream),
                var c when char.IsDigit(c) => DecodeString(c, stream),
                _ => throw new FormatException($"Invalid bencode in list")
            };
            list.Add(item);
        }
        return list;
    }

    private static Dictionary<string, object?> DecodeDict(Stream stream)
    {
        var dict = new Dictionary<string, object?>();
        while (true)
        {
            int peek = stream.ReadByte();
            if (peek == 'e' || peek == -1) break;

            // Keys are always strings
            if (!char.IsDigit((char)peek))
                throw new FormatException("Dict key must be string");

            var key = DecodeString((char)peek, stream);
            var value = Decode(stream);
            dict[key] = value;
        }
        return dict;
    }

    #endregion
}
