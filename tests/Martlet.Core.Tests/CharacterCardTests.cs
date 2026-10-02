using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class CharacterCardTests
{
    private const string V2 = """
        {"spec":"chara_card_v2","spec_version":"2.0","data":{"name":"Aria","description":"{{char}} is a wandering bard.\r\n{{user}} met her at an inn.",
        "personality":"cheerful, curious","scenario":"A rainy night with <USER>.","first_mes":"*waves* Hi {{user}}!",
        "mes_example":"<START>\n{{user}}: Sing?\n{{char}}: Always!","system_prompt":"{{original}}\nStay in character as {{char}}.",
        "post_history_instructions":"","creator_notes":"never in prompts","creator":"bardsmith","tags":["bard"],"extensions":{},
        "character_book":{"extensions":{},"entries":[
          {"keys":[],"content":"Aria's lute is named Echo.","extensions":{},"enabled":true,"insertion_order":0,"constant":true},
          {"keys":["inn"],"content":"The inn is the Gilded Goose.","extensions":{},"enabled":true,"insertion_order":1},
          {"keys":["x"],"content":"Disabled.","extensions":{},"enabled":false,"insertion_order":2,"constant":true}]}}}
        """;

    [Fact]
    public void PngCharaChunkBecomesPersona()
    {
        var card = CharacterCardReader.Read(Png(("chara", Base64(V2))));
        Assert.Equal(CharacterCardFormat.CharacterCardV2, card.Format);
        Assert.Equal("Aria", card.DisplayName);
        Assert.Equal("bardsmith", card.Creator);
        Assert.Single(card.AlwaysOnLore);
        Assert.Equal(1, card.KeywordLoreEntries);

        var persona = card.ToPersona("file", PersonaProfile.MaximumTextCharacters, PersonaProfile.MaximumTextUtf8Bytes);
        Assert.Equal("Aria", persona.Name);
        Assert.StartsWith("Stay in character as Aria.\n\nAria is a wandering bard.\nThe user met her at an inn.", persona.Text);
        Assert.Contains("Personality: cheerful, curious", persona.Text);
        Assert.Contains("Scenario: A rainy night with the user.", persona.Text);
        Assert.Contains("Background:\nAria's lute is named Echo.", persona.Text);
        Assert.Contains("Example dialogue:\nThe user: Sing?\nAria: Always!", persona.Text);
        Assert.EndsWith("Greeting:\n*waves* Hi the user!", persona.Text);
        Assert.DoesNotContain("never in prompts", persona.Text);
        Assert.DoesNotContain("{{", persona.Text);
        Assert.Empty(persona.Shortened);
        Assert.Empty(persona.LeftOut);
        PersonaProfile.ValidateText(persona.Text);
    }

    [Fact]
    public void PngPrefersCcv3AndItsNickname()
    {
        const string v3 = """{"spec":"chara_card_v3","spec_version":"3.0","data":{"name":"Aria of the Vale","nickname":"Ari","description":"{{char}} hums."}}""";
        var card = CharacterCardReader.Read(Png(("chara", Base64(V2)), ("ccv3", Base64(v3))));
        Assert.Equal(CharacterCardFormat.CharacterCardV3, card.Format);
        var persona = card.ToPersona(null, 8192, 16384);
        Assert.Equal("Ari", persona.Name);
        Assert.Equal("Ari hums.", persona.Text);
    }

    [Fact]
    public void CompressedITxtChunkIsRead()
    {
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Base64(V2));
        var data = new List<byte>();
        data.AddRange("chara\0"u8.ToArray());
        data.AddRange(new byte[] { 1, 0, 0, 0 });
        data.AddRange(compressed.ToArray());
        Assert.Equal("Aria", CharacterCardReader.Read(PngChunks(("iTXt", data.ToArray()))).Name);
    }

    [Fact]
    public void JsonAndCharxCardsAreRead()
    {
        var v1 = CharacterCardReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(
            "\uFEFF{\"name\":\"Bob\",\"description\":\"Gruff.\",\"personality\":\"\",\"scenario\":\"\",\"first_mes\":\"\",\"mes_example\":\"\"}")));
        Assert.Equal(CharacterCardFormat.TavernV1, v1.Format);
        Assert.Equal("Gruff.", v1.ToPersona(null, 8192, 16384).Text);

        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("card.json").Open());
            writer.Write("""{"spec":"chara_card_v3","spec_version":"3.0","data":{"name":"Cleo","description":"Calm."}}""");
        }
        archive.Position = 0;
        var charx = CharacterCardReader.Read(archive);
        Assert.Equal(CharacterCardFormat.CharacterCardV3, charx.Format);
        Assert.Equal("Cleo", charx.Name);
    }

    [Fact]
    public void ImagesWithoutCardsAndOtherFilesAreRejected()
    {
        var plain = Assert.Throws<CharacterCardException>(() => CharacterCardReader.Read(Png(("Software", "paint"u8.ToArray()))));
        Assert.Equal(CharacterCardReader.NoCardInImage, plain.Message);
        Assert.Throws<CharacterCardException>(() => CharacterCardReader.Read(new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0, 0])));
        Assert.Throws<CharacterCardException>(() => CharacterCardReader.Read(new MemoryStream("{\"hello\":1}"u8.ToArray())));
        Assert.Throws<CharacterCardException>(() => CharacterCardReader.Read(Png(("chara", "not base64 json!"u8.ToArray()))));
    }

    [Fact]
    public void LongCardsFitTheBudgetByPriority()
    {
        var description = string.Join(" ", Enumerable.Repeat("She tells long stories about the sea.", 60));
        var examples = string.Join("\n", Enumerable.Repeat("{{char}}: Another tale of waves and wind!", 200));
        var json = $$"""{"name":"Mara","description":"{{description}}","personality":"wistful","mes_example":"{{examples.Replace("\n", "\\n")}}","first_mes":"Ahoy."}""";
        var card = CharacterCardReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        var persona = card.ToPersona(null, 4000, 8000);
        Assert.True(persona.Text.Length <= 4000);
        Assert.StartsWith(description, persona.Text);
        Assert.Contains("Personality: wistful", persona.Text);
        Assert.Contains("Greeting:\nAhoy.", persona.Text);
        Assert.Equal(["example dialogue"], persona.Shortened);
        Assert.Contains("\u2026", persona.Text);

        var tight = card.ToPersona(null, 1000, 2000);
        Assert.True(tight.Text.Length <= 1000);
        Assert.Equal(["description"], tight.Shortened);
        Assert.Equal(["example dialogue"], tight.LeftOut);
        Assert.Contains("sea.\u2026\n\nPersonality: wistful\n\nGreeting:\nAhoy.", tight.Text);
    }

    [Fact]
    public void NamesAreMadeValidPersonaNames()
    {
        Assert.Equal("A B", CharacterCard.PersonaName("  A\n\tB  "));
        Assert.Equal(64, CharacterCard.PersonaName(new string('n', 100))!.Length);
        Assert.Null(CharacterCard.PersonaName(" \u0001 "));
        var card = new CharacterCard { Format = CharacterCardFormat.TavernV1, Name = "", Description = "Quiet." };
        Assert.Equal("my-card", card.ToPersona("my-card", 8192, 16384).Name);
    }

    private static byte[] Base64(string json) => Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));

    private static MemoryStream Png(params (string Keyword, byte[] Text)[] texts) =>
        PngChunks(texts.Select(t => ("tEXt", Encoding.Latin1.GetBytes(t.Keyword).Append((byte)0).Concat(t.Text).ToArray())).ToArray());

    private static MemoryStream PngChunks(params (string Type, byte[] Data)[] chunks)
    {
        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        Chunk("IDAT", new byte[64]);
        foreach (var (type, data) in chunks) Chunk(type, data);
        Chunk("IEND", []);
        png.Position = 0;
        return png;

        void Chunk(string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            png.Write(length);
            png.Write(Encoding.ASCII.GetBytes(type));
            png.Write(data);
            png.Write(new byte[4]);
        }
    }
}
