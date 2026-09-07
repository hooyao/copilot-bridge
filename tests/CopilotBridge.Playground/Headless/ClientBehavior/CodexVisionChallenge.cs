using System.Globalization;
using System.Security.Cryptography;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// A fresh pixel-only challenge for real-client vision. Neither its code nor the
/// randomized color positions are disclosed to Codex in text or in the filename.
/// The harness keeps the answer in memory until the client has exited.
/// </summary>
internal sealed record CodexVisionChallenge(byte[] Png, string ExpectedAnswer)
{
    private static readonly string[][] DigitRows =
    [
        ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
        ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
        ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ["01110", "10001", "10001", "01111", "00001", "00001", "01110"],
    ];

    internal static CodexVisionChallenge Create()
    {
        (string Name, byte R, byte G, byte B)[] colors =
        [
            ("red", 230, 20, 20),
            ("green", 0, 160, 0),
            ("blue", 0, 70, 230),
            ("yellow", 255, 215, 0),
        ];
        for (var index = colors.Length - 1; index > 0; index--)
        {
            var other = RandomNumberGenerator.GetInt32(index + 1);
            (colors[index], colors[other]) = (colors[other], colors[index]);
        }
        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000)
            .ToString(CultureInfo.InvariantCulture);

        (byte R, byte G, byte B) Pixel(int x, int y)
        {
            // Six large 5x7 glyphs, scaled 10x, centered above the four tiles.
            if (x >= 145 && x < 495 && y >= 40 && y < 110)
            {
                var column = (x - 145) / 10;
                var digitIndex = column / 6;
                var glyphColumn = column % 6;
                if (glyphColumn < 5 &&
                    DigitRows[code[digitIndex] - '0'][(y - 40) / 10][glyphColumn] == '1')
                    return (0, 0, 0);
            }

            for (var index = 0; index < colors.Length; index++)
            {
                var left = 32 + index % 2 * 304;
                var top = 160 + index / 2 * 192;
                if (x >= left && x < left + 272 && y >= top && y < top + 160)
                    return (colors[index].R, colors[index].G, colors[index].B);
            }
            return (255, 255, 255);
        }

        return new CodexVisionChallenge(
            PngGen.RgbPng(640, 560, Pixel),
            $"code={code}\n"
            + $"top-left={colors[0].Name}\n"
            + $"top-right={colors[1].Name}\n"
            + $"bottom-left={colors[2].Name}\n"
            + $"bottom-right={colors[3].Name}");
    }
}
