using System.Text;

namespace Shamash.Pc;

/// <summary>Text extraction for .rtf and Word 97-2003 .doc without Word. Pure managed code, works on any OS; used when Word COM is unavailable.</summary>
public static class LegacyDocReader
{
    static LegacyDocReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string ReadRtf(string path) => RtfToText(File.ReadAllBytes(path));

    public static string ReadDoc(string path) => DocToText(File.ReadAllBytes(path));

    // ---------------------------------------------------------------- RTF

    private static readonly HashSet<string> SkipDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf",
        "footnote", "object", "themedata", "colorschememapping", "latentstyles", "listtable", "listoverridetable", "rsidtbl", "datastore",
        "fldinst", "xmlnstbl", "generator", "private", "revtbl", "mmathPr", "bkmkstart", "bkmkend", "pgdsctbl", "template",
    };

    /// <summary>Plain text of an RTF document: \par/\line to newline, \tab/\cell to tab, \'hh bytes decoded with the document's \ansicpg (Hebrew: 1255), \uN escapes with their fallback skipped, font/colour/picture groups dropped.</summary>
    public static string RtfToText(byte[] data)
    {
        var s = Encoding.Latin1.GetString(data);
        var cp = 1252;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\\ansicpg(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var c)) cp = c;
        Encoding enc;
        try { enc = Encoding.GetEncoding(cp); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { enc = Encoding.GetEncoding(1252); }

        var sb = new StringBuilder();
        var pending = new List<byte>();
        void Flush() { if (pending.Count > 0) { sb.Append(enc.GetString(pending.ToArray())); pending.Clear(); } }

        // group state: skipping flag and the \ucN count
        var stack = new Stack<(bool Skip, int Uc)>();
        bool skip = false;
        int uc = 1, skipChars = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '{') { stack.Push((skip, uc)); continue; }
            if (ch == '}') { Flush(); if (stack.Count > 0) (skip, uc) = stack.Pop(); continue; }
            if (ch is '\r' or '\n') continue;
            if (ch != '\\')
            {
                if (skipChars > 0) { skipChars--; continue; }
                if (!skip) { Flush(); sb.Append(ch); }
                continue;
            }
            if (++i >= s.Length) break;
            var n = s[i];
            if (!char.IsAsciiLetter(n))
            {
                switch (n)
                {
                    case '\'':
                        if (i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                        {
                            var b = Convert.ToByte(s.Substring(i + 1, 2), 16);
                            i += 2;
                            if (skipChars > 0) skipChars--;
                            else if (!skip) pending.Add(b);
                        }
                        break;
                    case '*': // ignorable destination: skip the whole group
                        skip = true; break;
                    case '\\' or '{' or '}':
                        if (skipChars > 0) skipChars--; else if (!skip) { Flush(); sb.Append(n); }
                        break;
                    case '~': if (!skip) { Flush(); sb.Append('\u00A0'); } break;
                    case '_': if (!skip) { Flush(); sb.Append('-'); } break;
                }
                continue;
            }
            var start = i;
            while (i + 1 < s.Length && char.IsAsciiLetter(s[i + 1])) i++;
            var word = s.Substring(start, i - start + 1);
            var hasNum = false; var neg = false; var num = 0;
            if (i + 1 < s.Length && s[i + 1] == '-') { neg = true; i++; }
            while (i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])) { hasNum = true; num = Math.Min(num * 10 + (s[++i] - '0'), 1_000_000); }
            if (!hasNum) neg = false;
            if (i + 1 < s.Length && s[i + 1] == ' ') i++; // the delimiter space belongs to the word
            if (neg) num = -num;

            if (SkipDestinations.Contains(word)) { skip = true; continue; }
            if (skip) continue;
            switch (word)
            {
                case "par" or "line" or "row" or "sect" or "page": Flush(); sb.Append('\n'); break;
                case "tab" or "cell": Flush(); sb.Append('\t'); break;
                case "emdash": Flush(); sb.Append('\u2014'); break;
                case "endash": Flush(); sb.Append('\u2013'); break;
                case "lquote": Flush(); sb.Append('\u2018'); break;
                case "rquote": Flush(); sb.Append('\u2019'); break;
                case "ldblquote": Flush(); sb.Append('\u201C'); break;
                case "rdblquote": Flush(); sb.Append('\u201D'); break;
                case "bullet": Flush(); sb.Append('\u2022'); break;
                case "uc": uc = Math.Max(0, num); break;
                case "u":
                    Flush();
                    sb.Append((char)(ushort)(short)num);
                    skipChars = uc;
                    break;
            }
        }
        Flush();
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    // ---------------------------------------------------------------- Word 97-2003 binary

    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Main-document text of a .doc: OLE compound file, WordDocument stream, FIB, then the pieces of the CLX piece table in the 0Table/1Table stream. Encrypted files throw NotSupportedException.</summary>
    public static string DocToText(byte[] file)
    {
        if (file.Length < 512 || !file.AsSpan(0, 8).SequenceEqual(OleMagic)) throw new InvalidDataException("not an OLE compound file");
        var cfb = new Cfb(file);
        var word = cfb.Stream("WordDocument") ?? throw new InvalidDataException("no WordDocument stream");
        if (word.Length < 0x9A || BitConverter.ToUInt16(word, 0) != 0xA5EC) throw new InvalidDataException("not a Word 97 binary document");
        var flags = BitConverter.ToUInt16(word, 0x0A);
        if ((flags & 0x0100) != 0) throw new NotSupportedException("encrypted document");
        var table = cfb.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("no table stream");

        var csw = BitConverter.ToUInt16(word, 32);
        var pos = 34 + csw * 2;                    // cslw
        var cslw = BitConverter.ToUInt16(word, pos);
        var ccpText = BitConverter.ToInt32(word, pos + 2 + 3 * 4);
        pos += 2 + cslw * 4;                       // cbRgFcLcb
        var fcClx = BitConverter.ToInt32(word, pos + 2 + 33 * 8);
        var lcbClx = BitConverter.ToInt32(word, pos + 2 + 33 * 8 + 4);
        if (fcClx < 0 || lcbClx <= 0 || fcClx + lcbClx > table.Length) throw new InvalidDataException("bad piece table");

        var p = fcClx;
        var end = fcClx + lcbClx;
        while (p < end && table[p] == 0x01) p += 3 + BitConverter.ToUInt16(table, p + 1);   // Prc: skip property modifiers
        if (p >= end || table[p] != 0x02) throw new InvalidDataException("no piece table");
        var plcLen = BitConverter.ToInt32(table, p + 1);
        p += 5;
        var n = (plcLen - 4) / 12;
        var cp1252 = Encoding.GetEncoding(1252);
        var sb = new StringBuilder();
        for (var i = 0; i < n && sb.Length < ccpText; i++)
        {
            var cpStart = BitConverter.ToInt32(table, p + i * 4);
            var cpEnd = BitConverter.ToInt32(table, p + (i + 1) * 4);
            var fc = BitConverter.ToUInt32(table, p + (n + 1) * 4 + i * 8 + 2);
            var len = Math.Min(cpEnd - cpStart, ccpText - sb.Length);
            if (len <= 0) continue;
            if ((fc & 0x40000000) != 0)
            {
                var off = (int)((fc & 0x3FFFFFFF) / 2);
                if (off + len <= word.Length) sb.Append(cp1252.GetString(word, off, len));
            }
            else if ((long)fc + len * 2 <= word.Length) sb.Append(Encoding.Unicode.GetString(word, (int)fc, len * 2));
        }
        return CleanWordText(sb.ToString());
    }

    /// <summary>Word control characters: \r paragraph, \a cell/row end, \v line break, \f page break; field instructions (between 0x13 and 0x14) dropped, results kept.</summary>
    internal static string CleanWordText(string raw)
    {
        var sb = new StringBuilder();
        var fieldDepth = 0;
        var inInstr = new Stack<bool>();
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\u0013': inInstr.Push(true); break;
                case '\u0014': if (inInstr.Count > 0) { inInstr.Pop(); inInstr.Push(false); } break;
                case '\u0015': if (inInstr.Count > 0) inInstr.Pop(); break;
                default:
                    fieldDepth = inInstr.Count(x => x);
                    if (fieldDepth > 0) break;
                    if (c == '\r' || c == '\u000b' || c == '\f') sb.Append('\n');
                    else if (c == '\a') sb.Append('\t');
                    else if (c >= ' ' || c == '\t' || c == '\n') sb.Append(c);
                    break;
            }
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Just enough of the OLE compound file format ([MS-CFB]) to read named streams.</summary>
    private sealed class Cfb
    {
        private readonly byte[] _d;
        private readonly int _secSize, _miniSize, _cutoff;
        private readonly List<int> _fat = [];
        private readonly List<int> _miniFat = [];
        private readonly List<(string Name, byte Type, int Start, long Size)> _dir = [];
        private byte[] _miniStream = [];

        public Cfb(byte[] d)
        {
            _d = d;
            _secSize = 1 << BitConverter.ToUInt16(d, 30);
            _miniSize = 1 << BitConverter.ToUInt16(d, 32);
            _cutoff = BitConverter.ToInt32(d, 56);
            var difat = new List<int>();
            for (var i = 0; i < 109; i++) difat.Add(BitConverter.ToInt32(d, 76 + i * 4));
            var dSec = BitConverter.ToInt32(d, 68);
            for (var k = BitConverter.ToInt32(d, 72); k > 0 && dSec >= 0; k--)
            {
                var o = Off(dSec);
                for (var i = 0; i < _secSize / 4 - 1; i++) difat.Add(BitConverter.ToInt32(d, o + i * 4));
                dSec = BitConverter.ToInt32(d, o + _secSize - 4);
            }
            foreach (var s in difat.Where(s => s >= 0))
                for (var i = 0; i < _secSize / 4; i++) _fat.Add(BitConverter.ToInt32(d, Off(s) + i * 4));

            foreach (var s in Chain(BitConverter.ToInt32(d, 60)))
                for (var i = 0; i < _secSize / 4; i++) _miniFat.Add(BitConverter.ToInt32(d, Off(s) + i * 4));

            var dir = Read(BitConverter.ToInt32(d, 48), long.MaxValue);
            for (var o = 0; o + 128 <= dir.Length; o += 128)
            {
                var nameLen = BitConverter.ToUInt16(dir, o + 64);
                var name = nameLen >= 2 ? Encoding.Unicode.GetString(dir, o, nameLen - 2) : "";
                _dir.Add((name, dir[o + 66], BitConverter.ToInt32(dir, o + 116), BitConverter.ToUInt32(dir, o + 120)));
            }
            if (_dir.Count > 0) _miniStream = Read(_dir[0].Start, _dir[0].Size);
        }

        private int Off(int sector) => (sector + 1) * _secSize;

        private IEnumerable<int> Chain(int start)
        {
            for (int s = start, guard = 0; s >= 0 && s < _fat.Count && Off(s) + _secSize <= _d.Length && guard++ <= _fat.Count; s = _fat[s]) yield return s;
        }

        private byte[] Read(int start, long size)
        {
            var ms = new MemoryStream();
            foreach (var s in Chain(start)) { ms.Write(_d, Off(s), _secSize); if (ms.Length >= size) break; }
            var all = ms.ToArray();
            return size < all.Length ? all[..(int)size] : all;
        }

        public byte[]? Stream(string name)
        {
            foreach (var e in _dir)
            {
                if (e.Type != 2 || e.Name != name) continue;
                if (e.Size >= _cutoff) return Read(e.Start, e.Size);
                var ms = new MemoryStream();
                for (var s = e.Start; s >= 0 && s < _miniFat.Count && ms.Length < e.Size; s = _miniFat[s])
                {
                    var o = s * _miniSize;
                    if (o + _miniSize > _miniStream.Length) break;
                    ms.Write(_miniStream, o, _miniSize);
                }
                var all = ms.ToArray();
                return e.Size < all.Length ? all[..(int)e.Size] : all;
            }
            return null;
        }
    }
}
