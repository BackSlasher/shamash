using System.Text;
using System.Text.Json;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class OfficeTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Esc(string s) => JsonSerializer.Serialize(s);

    // ---- RTF

    [Fact]
    public void Rtf_HebrewCp1255HexEscapes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shalom = string.Concat(Encoding.GetEncoding(1255).GetBytes("שלום").Select(b => $"\\'{b:x2}"));
        var rtf = @"{\rtf1\ansi\ansicpg1255\deff0{\fonttbl{\f0\fnil\fcharset177 Arial;}}{\colortbl;\red0\green0\blue0;}" +
                  @"{\*\generator Riched20;}\pard\rtlpar\f0 " + shalom + @"\~\par second\tab line\line third \u1488\'e0 end\par}";
        var text = LegacyDocReader.RtfToText(Encoding.ASCII.GetBytes(rtf));
        Assert.StartsWith("שלום", text);
        Assert.Contains("second\tline\nthird א end", text);
        Assert.DoesNotContain("Arial", text);
        Assert.DoesNotContain("Riched", text);
    }

    [Fact]
    public void Rtf_UnicodeFallbackSkippedAndEscapedBraces()
    {
        var text = LegacyDocReader.RtfToText(Encoding.ASCII.GetBytes(@"{\rtf1\ansi\uc1 a\u1513?b \{x\} {\pict\wbitmap0 FFFF}c\par}"));
        Assert.Equal("aשb {x} c", text);
    }

    [Fact]
    public void Rtf_NoCodepageDefaultsTo1252()
    {
        Assert.Equal("café", LegacyDocReader.RtfToText(Encoding.ASCII.GetBytes(@"{\rtf1\ansi caf\'e9}")));
    }

    [Fact]
    public async Task ReadDocument_ReadsRtfAndDoc()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.rtf"), @"{\rtf1\ansi\ansicpg1255 \'f9\'ec\'e5\'ed\par}", Encoding.ASCII);
        File.WriteAllBytes(d.File("b.doc"), MakeDoc("מכתב לעירייה\rשורה שנייה\r", "plain ansi é\r"));
        var tool = new ReadDocumentTool([d.Path]);
        Assert.Equal("שלום", await tool.RunAsync(J(JsonSerializer.Serialize(new { path = Path.Combine(d.Path, "a.rtf") })), default));
        var doc = await tool.RunAsync(J(JsonSerializer.Serialize(new { path = Path.Combine(d.Path, "b.doc") })), default);
        Assert.Equal("מכתב לעירייה\nשורה שנייה\nplain ansi é", doc);
    }

    [Fact]
    public void Doc_RejectsNonOleAndEncrypted()
    {
        Assert.Throws<InvalidDataException>(() => LegacyDocReader.DocToText(new byte[2000]));
        var enc = MakeDoc("x\r", flags: 0x0300 | 0x0100);
        Assert.Throws<NotSupportedException>(() => LegacyDocReader.DocToText(enc));
    }

    [Fact]
    public void Doc_FieldInstructionsDroppedControlCharsMapped()
    {
        var doc = MakeDoc("a\u0013 HYPERLINK \"x\" \u0014link\u0015 b\ac\u000bd\r");
        Assert.Equal("alink b\tc\nd", LegacyDocReader.DocToText(doc));
    }

    /// <summary>A synthetic Word 97 .doc: OLE v3 compound file with WordDocument and 1Table streams of 4096 bytes each; one Unicode piece and an optional compressed (cp1252) piece.</summary>
    internal static byte[] MakeDoc(string unicodeText, string? ansiText = null, int flags = 0x0200)
    {
        const int Sec = 512, WordSec = 8;
        var uni = Encoding.Unicode.GetBytes(unicodeText);
        var ansi = ansiText is null ? [] : Encoding.GetEncoding(1252).GetBytes(ansiText);
        var word = new byte[4096];
        BitConverter.GetBytes((ushort)0xA5EC).CopyTo(word, 0);
        BitConverter.GetBytes((ushort)flags).CopyTo(word, 0x0A);
        BitConverter.GetBytes((ushort)14).CopyTo(word, 32);          // csw
        var pos = 34 + 14 * 2;
        BitConverter.GetBytes((ushort)22).CopyTo(word, pos);          // cslw
        var ccp = unicodeText.Length + (ansiText?.Length ?? 0);
        BitConverter.GetBytes(ccp).CopyTo(word, pos + 2 + 3 * 4);     // ccpText
        pos += 2 + 22 * 4;
        BitConverter.GetBytes((ushort)93).CopyTo(word, pos);          // cbRgFcLcb
        uni.CopyTo(word, 1024);
        ansi.CopyTo(word, 1024 + uni.Length);

        var pieces = ansiText is null ? 1 : 2;
        var table = new byte[4096];
        var clx = new List<byte> { 0x01, 0x00, 0x00, 0x02 };            // empty Prc (cbGrpprl 0), then Pcdt
        clx.AddRange(BitConverter.GetBytes(4 + pieces * 12));
        clx.AddRange(BitConverter.GetBytes(0));
        clx.AddRange(BitConverter.GetBytes(unicodeText.Length));
        if (ansiText is not null) clx.AddRange(BitConverter.GetBytes(ccp));
        clx.AddRange([0, 0]); clx.AddRange(BitConverter.GetBytes(1024u)); clx.AddRange([0, 0]);
        if (ansiText is not null)
        {
            clx.AddRange([0, 0]); clx.AddRange(BitConverter.GetBytes((uint)((1024 + uni.Length) * 2) | 0x40000000u)); clx.AddRange([0, 0]);
        }
        clx.CopyTo(table, 100);
        BitConverter.GetBytes(100).CopyTo(word, pos + 2 + 33 * 8);
        BitConverter.GetBytes(clx.Count).CopyTo(word, pos + 2 + 33 * 8 + 4);

        // sectors: 0 FAT, 1 directory, 2..9 WordDocument, 10..17 1Table
        var f = new byte[Sec * (1 + 18)];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(f, 0);
        BitConverter.GetBytes((ushort)3).CopyTo(f, 26);
        BitConverter.GetBytes((ushort)9).CopyTo(f, 30);
        BitConverter.GetBytes((ushort)6).CopyTo(f, 32);
        BitConverter.GetBytes(1).CopyTo(f, 44);                        // FAT sectors
        BitConverter.GetBytes(1).CopyTo(f, 48);                        // first dir sector
        BitConverter.GetBytes(4096).CopyTo(f, 56);
        BitConverter.GetBytes(-2).CopyTo(f, 60);                       // no mini FAT
        BitConverter.GetBytes(-2).CopyTo(f, 68);
        for (var i = 0; i < 109; i++) BitConverter.GetBytes(-1).CopyTo(f, 76 + i * 4);
        BitConverter.GetBytes(0).CopyTo(f, 76);                        // DIFAT[0] = sector 0

        var fat = new int[128];
        Array.Fill(fat, -1);
        fat[0] = -3; fat[1] = -2;
        for (var i = 0; i < WordSec; i++) fat[2 + i] = i == WordSec - 1 ? -2 : 3 + i;
        for (var i = 0; i < WordSec; i++) fat[10 + i] = i == WordSec - 1 ? -2 : 11 + i;
        for (var i = 0; i < 128; i++) BitConverter.GetBytes(fat[i]).CopyTo(f, Sec + i * 4);

        void Entry(int idx, string name, byte type, int start, int size)
        {
            var o = Sec * 2 + idx * 128;
            Encoding.Unicode.GetBytes(name).CopyTo(f, o);
            BitConverter.GetBytes((ushort)((name.Length + 1) * 2)).CopyTo(f, o + 64);
            f[o + 66] = type;
            BitConverter.GetBytes(start).CopyTo(f, o + 116);
            BitConverter.GetBytes(size).CopyTo(f, o + 120);
        }
        Entry(0, "Root Entry", 5, -2, 0);
        Entry(1, "WordDocument", 2, 2, 4096);
        Entry(2, "1Table", 2, 10, 4096);
        word.CopyTo(f, Sec * 3);
        table.CopyTo(f, Sec * 11);
        return f;
    }

    // ---- close_document, sort target_root, mail pure parts

    [Fact]
    public async Task CloseDocument_GuardsThenWindowsOnly()
    {
        using var d = new TempDir();
        using var other = new TempDir();
        File.WriteAllText(other.File("x.txt"), "x");
        File.WriteAllText(d.File("a.docx"), "x");
        var t = new CloseDocumentTool([d.Path]);
        Assert.Equal("error: path is required", await t.RunAsync(J("{}"), default));
        Assert.Equal("error: save must be ask, yes or no", await t.RunAsync(J(@"{""path"":""a.docx"",""save"":""maybe""}"), default));
        Assert.Equal("error: path not under a document root", await t.RunAsync(J(JsonSerializer.Serialize(new { path = Path.Combine(other.Path, "x.txt") })), default));
        Assert.Equal("error: file not found", await t.RunAsync(J(JsonSerializer.Serialize(new { path = Path.Combine(d.Path, "no.docx") })), default));
        if (!OperatingSystem.IsWindows())
            Assert.Equal("error: Windows only", await t.RunAsync(J(JsonSerializer.Serialize(new { path = Path.Combine(d.Path, "a.docx") })), default));
    }

    [Theory]
    [InlineData(null, "ask")] [InlineData("", "ask")] [InlineData("YES", "yes")] [InlineData(" no ", "no")] [InlineData("x", null)]
    public void NormalizeSave(string? input, string? expected) => Assert.Equal(expected, CloseDocumentTool.NormalizeSave(input));

    [Fact]
    public void OfficeCom_TimeoutAndErrors()
    {
        Assert.Equal(5, OfficeCom.RunSta(() => 5, 1000));
        var e = Assert.Throws<TimeoutException>(() => OfficeCom.RunSta(() => { Thread.Sleep(3000); return 1; }, 100));
        Assert.StartsWith("error: Office did not answer", OfficeCom.Describe(e));
        Assert.Contains("not installed", OfficeCom.Describe(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80040154))));
        Assert.Contains("0x800706BA", OfficeCom.Describe(new System.Runtime.InteropServices.COMException("RPC down", unchecked((int)0x800706BA))));
    }

    [Fact]
    public void OfficeCom_AppendsDialogToError_ViaProbe()
    {
        var dlg = new DialogInfo("Word", "Microsoft Word", ["Word found unreadable content."], ["Yes", "No"]);
        var e = new TimeoutException("Office did not answer within 25 s (a dialog may be waiting on screen)");
        var s = OfficeCom.DescribeWithDialogs(e, () => [dlg]);
        Assert.StartsWith("error: Office did not answer", s);
        Assert.EndsWith("; Word is showing a dialog \"Microsoft Word\": Word found unreadable content. [buttons: Yes, No]", s);
        Assert.Equal("error: x", OfficeCom.AppendDialog("error: x", () => []));
        Assert.Equal("error: x", OfficeCom.AppendDialog("error: x", () => throw new InvalidOperationException("probe broke")));
    }

    [Fact]
    public async Task OutlookTools_WindowsOnlyElsewhere_AndGuards()
    {
        var tools = OutlookTools.Create().ToDictionary(t => t.Name);
        Assert.Equal(["list_mail_accounts", "list_recent_mail", "read_mail", "draft_reply", "send_draft"], tools.Keys.ToArray());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("error: Windows only", await tools["list_mail_accounts"].RunAsync(J("{}"), default));
            Assert.Equal("error: Windows only", await tools["list_recent_mail"].RunAsync(J("{}"), default));
        }
        Assert.Equal("error: id is required", await tools["read_mail"].RunAsync(J("{}"), default));
        Assert.StartsWith("error: unknown id", await tools["read_mail"].RunAsync(J(@"{""id"":""m9""}"), default));
        Assert.Equal("error: text is required", await tools["draft_reply"].RunAsync(J(@"{""id"":""m1""}"), default));
        Assert.Equal("error: not a draft made by draft_reply", await tools["send_draft"].RunAsync(J(@"{""id"":""m1""}"), default));
    }

    [Fact]
    public void MailIdMap_StableIdsAndDraftMarks()
    {
        var m = new MailIdMap();
        var a = m.Add("ENTRY-A", "S1");
        var b = m.Add("ENTRY-B", "S2");
        Assert.NotEqual(a, b);
        Assert.Equal(a, m.Add("ENTRY-A", "S1"));
        Assert.True(m.TryGet(b.ToUpperInvariant(), out var e, out var s));
        Assert.Equal(("ENTRY-B", "S2"), (e, s));
        Assert.False(m.TryGet("m99", out _, out _));
        Assert.False(m.IsDraft(a));
        m.MarkDraft(a);
        Assert.True(m.IsDraft(a));
    }

    [Fact]
    public void MailText_SnippetBodyAndHtml()
    {
        Assert.Equal("a b c", MailText.Snippet("  a\r\n\r\n b\t\tc  "));
        var s = MailText.Snippet(new string('x', 300));
        Assert.Equal(203, s.Length);
        Assert.EndsWith("...", s);
        Assert.Equal("a\n\nb", MailText.Body("a\r\n\r\n\r\n\r\nb"));
        Assert.StartsWith("xxx", MailText.Body(new string('x', 7000)));
        Assert.Contains("[truncated, 7000 chars total]", MailText.Body(new string('x', 7000)));
        var html = MailText.InsertIntoHtml("<html><body lang=HE><p>quoted</p></body></html>", "שלום <b>\nשורה");
        Assert.Equal("<html><body lang=HE><div dir=\"auto\">שלום &lt;b&gt;<br>שורה</div><br><p>quoted</p></body></html>", html);
    }

    [Fact]
    public void SortTargetRoot_AcceptsRootNamesHebrewNamesAndPaths()
    {
        using var d = new TempDir();
        var desk = Path.Combine(d.Path, "Desktop");
        var docs = Path.Combine(d.Path, "Documents");
        Directory.CreateDirectory(Path.Combine(desk, "salad"));
        Directory.CreateDirectory(Path.Combine(docs, "Letters"));
        var store = new SortStore(Path.Combine(d.Path, "s.json"), new OrganizeStore(Path.Combine(d.Path, "m.log")), [desk, docs], 3000);
        foreach (var a in new[] { "Documents", "documents", "מסמכים", docs, "Letters" })
            Assert.Equal(a == "Letters" ? Path.Combine(docs, "Letters") : docs, store.ResolveTarget(a));
        foreach (var a in new[] { "Desktop", "DESKTOP", "שולחן העבודה", "שולחן עבודה" })
            Assert.Equal(desk, store.ResolveTarget(a));
        Assert.Null(store.ResolveTarget("nope"));
        Assert.Null(store.ResolveTarget(d.Path));

        var r = store.Start(J(JsonSerializer.Serialize(new { folder = Path.Combine(desk, "salad"), target_root = "מסמכים" })));
        Assert.Contains("target_root: Documents", r);
        var def = store.Start(J(JsonSerializer.Serialize(new { folder = Path.Combine(desk, "salad") })));
        Assert.Contains("target_root: Desktop", def);
    }

    [Fact]
    public void BuildTools_RegistersOfficeTools()
    {
        var names = TurnPipeline.BuildTools(ShamashSettings.Load(_ => null, [])).Select(t => t.Name).ToList();
        Assert.All(new[] { "close_document", "list_mail_accounts", "list_recent_mail", "read_mail", "draft_reply", "send_draft" }, n => Assert.Contains(n, names));
    }
}
