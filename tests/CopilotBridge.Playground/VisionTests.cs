using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies image content blocks round-trip through Copilot's <c>/v1/messages</c>
/// when accompanied by the <c>Copilot-Vision-Request: true</c> header. We don't
/// assert on the model's interpretation of the image — only that the request
/// succeeds and produces a text response. Wire-format compatibility is what the
/// bridge needs.
/// </summary>
[Trait("Category", "Integration")]
public class VisionTests
{
    [Theory]
    [InlineData("claude-sonnet-4.6")]
    public async Task ImageContentBlock_RoundTripsToTextResponse(string model)
    {
        // 100×100 red PNG. Anthropic's documented vision pipeline rejects very small
        // images ("could not process image"); a few-thousand-byte PNG is large enough.
        var pngBase64 = Convert.ToBase64String(PngGen.SolidRgbPng(100, 100, 255, 0, 0));

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/png",
                                ["data"] = pngBase64,
                            },
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Briefly describe what you see. One sentence.",
                        },
                    },
                },
            },
            ["max_tokens"] = 64,
        };

        using var client = new PlaygroundClient();
        var responseBody = await client.PostMessagesAsync(payload.ToJsonString(), vision: true);
        var response = JsonNode.Parse(responseBody)!;

        Assert.Equal("end_turn", response["stop_reason"]?.GetValue<string>());

        var textBlock = response["content"]!.AsArray()
            .FirstOrDefault(b => b?["type"]?.GetValue<string>() == "text");
        Assert.NotNull(textBlock);
        Assert.False(string.IsNullOrWhiteSpace(textBlock!["text"]!.GetValue<string>()));
    }

    /// <summary>
    /// Tiny in-memory PNG encoder. Pure BCL (DeflateStream + raw chunk writing).
    /// Generates a 24-bit RGB image of the given size filled with one solid color.
    /// </summary>
    private static class PngGen
    {
        private static readonly uint[] CrcTable = BuildCrcTable();
        private const uint Crc32Polynomial = 0xEDB88320u;

        public static byte[] SolidRgbPng(int width, int height, byte r, byte g, byte b)
        {
            using var ms = new MemoryStream();
            // PNG signature
            ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            // IHDR chunk
            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 2;   // color type: truecolor RGB
            ihdr[10] = 0;  // compression
            ihdr[11] = 0;  // filter
            ihdr[12] = 0;  // interlace
            WriteChunk(ms, "IHDR", ihdr);

            // Raw scanlines: each row prefixed with a 0x00 filter byte, then RGB triplets.
            var raw = new byte[height * (1 + width * 3)];
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * (1 + width * 3);
                for (int x = 0; x < width; x++)
                {
                    int p = rowStart + 1 + x * 3;
                    raw[p] = r;
                    raw[p + 1] = g;
                    raw[p + 2] = b;
                }
            }

            // Zlib-compress (DEFLATE wrapped in zlib header + Adler-32).
            using var compressed = new MemoryStream();
            compressed.WriteByte(0x78); // zlib header (deflate, default compression)
            compressed.WriteByte(0x9C);
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw);
            }
            Span<byte> adlerBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(adlerBytes, Adler32(raw));
            compressed.Write(adlerBytes);

            WriteChunk(ms, "IDAT", compressed.ToArray());
            WriteChunk(ms, "IEND", []);
            return ms.ToArray();
        }

        private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> lenBytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
            s.Write(lenBytes);

            var typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);

            // CRC32 over type + data, in that order.
            uint crc = 0xFFFFFFFFu;
            foreach (var x in typeBytes) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            foreach (var x in data) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            crc ^= 0xFFFFFFFFu;

            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            s.Write(crcBytes);
        }

        private static uint Adler32(ReadOnlySpan<byte> data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            foreach (var x in data)
            {
                a = (a + x) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) == 1 ? Crc32Polynomial ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }
}
