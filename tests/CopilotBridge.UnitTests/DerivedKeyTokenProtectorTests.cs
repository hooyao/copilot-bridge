using System.Security.Cryptography;
using System.Text;
using CopilotBridge.Cli.Auth;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Exercises the cross-platform (non-Windows) token protector on Windows by injecting a fixed
/// machine-key provider. AES-256-CBC + HMAC-SHA256 + HKDF are all CNG-backed here, so the byte-level
/// behavior is identical to what runs on Linux/macOS — only the real machine-id <i>source</i> differs
/// (covered separately by <see cref="MachineKeyProvider.ParseIOPlatformUUID"/> tests + CI smoke).
/// </summary>
public class DerivedKeyTokenProtectorTests
{
    private sealed class FixedKeyProvider(byte[] material) : IMachineKeyProvider
    {
        public byte[] GetKeyMaterial() => material;
    }

    private static DerivedKeyTokenProtector Protector(string material = "machine-Aalicesalt") =>
        new(new FixedKeyProvider(Encoding.UTF8.GetBytes(material)));

    private const int IvLength = 16;
    private const int MacLength = 32;
    private const int HeaderLength = 1; // version byte

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("gho_0123456789abcdefABCDEF0123456789abcdef")]
    [InlineData("token-with-unicode-中文-\U0001F600-emoji")]
    public void RoundTrips(string token)
    {
        var protector = Protector();
        var plaintext = Encoding.UTF8.GetBytes(token);

        var blob = protector.Protect(plaintext);
        var recovered = protector.Unprotect(blob);

        Assert.Equal(plaintext, recovered);
        Assert.Equal(token, Encoding.UTF8.GetString(recovered));
    }

    [Fact]
    public void LongTokenRoundTrips()
    {
        var protector = Protector();
        var plaintext = Encoding.UTF8.GetBytes(new string('A', 8192));

        var recovered = protector.Unprotect(protector.Protect(plaintext));

        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void ProtectUsesFreshIv_SameInputDiffersButBothDecrypt()
    {
        var protector = Protector();
        var plaintext = Encoding.UTF8.GetBytes("same-token");

        var blob1 = protector.Protect(plaintext);
        var blob2 = protector.Protect(plaintext);

        Assert.NotEqual(blob1, blob2); // random IV → different ciphertext + MAC
        Assert.Equal(plaintext, protector.Unprotect(blob1));
        Assert.Equal(plaintext, protector.Unprotect(blob2));
    }

    [Fact]
    public void BlobLayout_IsVersionIvCiphertextMac()
    {
        var protector = Protector();
        var plaintext = Encoding.UTF8.GetBytes("hello"); // 5 bytes → one 16-byte AES-CBC block

        var blob = protector.Protect(plaintext);

        Assert.Equal(0x01, blob[0]); // version
        // 1 (version) + 16 (IV) + 16 (one padded block) + 32 (MAC) = 65
        Assert.Equal(HeaderLength + IvLength + 16 + MacLength, blob.Length);
    }

    [Fact]
    public void WrongMachine_FailsToDecrypt()
    {
        var saved = Protector("machine-Aalicesalt").Protect(Encoding.UTF8.GetBytes("secret"));
        var other = Protector("machine-Balicesalt");

        Assert.Throws<CryptographicException>(() => other.Unprotect(saved));
    }

    [Fact]
    public void WrongUser_FailsToDecrypt()
    {
        var saved = Protector("machine-Aalicesalt").Protect(Encoding.UTF8.GetBytes("secret"));
        var other = Protector("machine-Abobsalt");

        Assert.Throws<CryptographicException>(() => other.Unprotect(saved));
    }

    [Fact]
    public void TamperedCiphertext_FailsMac()
    {
        var protector = Protector();
        var blob = protector.Protect(Encoding.UTF8.GetBytes("secret"));

        blob[HeaderLength + IvLength] ^= 0xFF; // flip a ciphertext byte

        Assert.Throws<CryptographicException>(() => protector.Unprotect(blob));
    }

    [Fact]
    public void TamperedIv_FailsMac()
    {
        var protector = Protector();
        var blob = protector.Protect(Encoding.UTF8.GetBytes("secret"));

        blob[HeaderLength] ^= 0xFF; // flip an IV byte (MAC covers the IV)

        Assert.Throws<CryptographicException>(() => protector.Unprotect(blob));
    }

    [Fact]
    public void TamperedMac_Fails()
    {
        var protector = Protector();
        var blob = protector.Protect(Encoding.UTF8.GetBytes("secret"));

        blob[^1] ^= 0xFF; // flip the last MAC byte

        Assert.Throws<CryptographicException>(() => protector.Unprotect(blob));
    }

    [Fact]
    public void UnknownVersion_Fails()
    {
        var protector = Protector();
        var blob = protector.Protect(Encoding.UTF8.GetBytes("secret"));

        blob[0] = 0x02; // bump version

        Assert.Throws<CryptographicException>(() => protector.Unprotect(blob));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(HeaderLength + IvLength + MacLength - 1)] // one short of the minimum
    public void TruncatedBlob_Fails(int length)
    {
        var protector = Protector();
        Assert.Throws<CryptographicException>(() => protector.Unprotect(new byte[length]));
    }
}

/// <summary>
/// The <c>ioreg</c> output parsing is a pure string transform, unit-testable on Windows from a
/// captured sample (the <c>ioreg</c> exec itself only runs on macOS).
/// </summary>
public class MachineKeyProviderParseTests
{
    [Fact]
    public void ParsesIOPlatformUUID_FromRealisticSample()
    {
        const string sample = """
        +-o IOPlatformExpertDevice  <class IOPlatformExpertDevice, id 0x100000123, registered>
            {
              "IOPlatformSerialNumber" = "C02XYZ123ABC"
              "IOPlatformUUID" = "12345678-90AB-CDEF-1234-567890ABCDEF"
              "IOPlatformSystemSleepPolicy" = <0123>
            }
        """;

        Assert.Equal("12345678-90AB-CDEF-1234-567890ABCDEF",
            MachineKeyProvider.ParseIOPlatformUUID(sample));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no uuid here")]
    [InlineData("\"IOPlatformUUID\" = ")]          // key present, no quoted value
    [InlineData("\"IOPlatformUUID\" = \"\"")]      // empty value
    public void ReturnsNull_WhenAbsentOrEmpty(string input)
    {
        Assert.Null(MachineKeyProvider.ParseIOPlatformUUID(input));
    }
}
