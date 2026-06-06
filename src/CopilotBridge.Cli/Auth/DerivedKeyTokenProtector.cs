using System.Security.Cryptography;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Cross-platform token protector for systems without DPAPI (Linux, macOS). Encrypts the token with
/// <b>AES-256-CBC</b> and authenticates with <b>HMAC-SHA256</b> in an Encrypt-then-MAC construction,
/// using keys derived from machine + user identity via <b>HKDF-SHA256</b>.
/// <para>
/// All three primitives are FIPS-approved: AES (FIPS-197 / SP800-38A CBC), HMAC-SHA256
/// (FIPS-198-1 / FIPS-180-4), HKDF (SP800-56C). AES-GCM is deliberately avoided — on macOS it has
/// historically required OpenSSL, whereas AES-CBC + HMAC route through the platform's native crypto
/// (Apple CommonCrypto / Windows CNG / OpenSSL libcrypto) with no optional dependency, and are
/// byte-for-byte reproducible on Windows for unit testing.
/// </para>
/// <para><b>On-disk blob layout</b> (see <see cref="Protect"/>):</para>
/// <code>
/// | 0       | 1 .. 16 | 17 .. (N-33) | (N-32) .. (N-1) |
/// | version | IV (16) | ciphertext   | HMAC-SHA256(32) |
/// </code>
/// <para>
/// <b>Security note:</b> this is weaker than DPAPI/Keychain. The key is derived from the machine id
/// (readable, e.g. <c>/etc/machine-id</c>) plus the username — not a hardware-backed secret. It
/// defends against the token file being copied to another machine and against casual disclosure, but
/// a local attacker running as the same user on the same host could re-derive the key. It is, however,
/// never plaintext. See <c>docs/token-storage.md</c>.
/// </para>
/// </summary>
internal sealed class DerivedKeyTokenProtector : ITokenProtector
{
    private const byte Version = 0x01;
    private const int IvLength = 16;   // AES block size
    private const int MacLength = 32;  // HMAC-SHA256 output
    private const int KeyLength = 32;  // AES-256 / HMAC key
    private const int MinBlobLength = 1 + IvLength + MacLength;

    // HKDF context. The salt is a fixed, non-secret app constant; the two info strings derive
    // independent keys for encryption vs authentication from the same input keying material.
    private static readonly byte[] s_hkdfSalt = "copilot-bridge.tokenstore.hkdf.salt.v1"u8.ToArray();
    private static readonly byte[] s_infoEnc = "copilot-bridge:aes-256-cbc:v1"u8.ToArray();
    private static readonly byte[] s_infoMac = "copilot-bridge:hmac-sha256:v1"u8.ToArray();

    private readonly IMachineKeyProvider _keyProvider;

    public DerivedKeyTokenProtector(IMachineKeyProvider keyProvider) => _keyProvider = keyProvider;

    public byte[] Protect(byte[] plaintext)
    {
        var (encKey, macKey) = DeriveKeys();
        try
        {
            var iv = RandomNumberGenerator.GetBytes(IvLength);
            byte[] ciphertext;
            using (var aes = Aes.Create())
            {
                aes.Key = encKey;
                ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
            }

            var blob = new byte[1 + IvLength + ciphertext.Length + MacLength];
            blob[0] = Version;
            Buffer.BlockCopy(iv, 0, blob, 1, IvLength);
            Buffer.BlockCopy(ciphertext, 0, blob, 1 + IvLength, ciphertext.Length);

            // Encrypt-then-MAC: authenticate version + IV + ciphertext (everything before the tag).
            var macInput = blob.AsSpan(0, 1 + IvLength + ciphertext.Length);
            var mac = HMACSHA256.HashData(macKey, macInput);
            Buffer.BlockCopy(mac, 0, blob, 1 + IvLength + ciphertext.Length, MacLength);

            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    public byte[] Unprotect(byte[] blob)
    {
        if (blob.Length < MinBlobLength)
            throw new CryptographicException("Token blob is too short.");
        if (blob[0] != Version)
            throw new CryptographicException($"Unsupported token blob version: {blob[0]}.");

        var (encKey, macKey) = DeriveKeys();
        try
        {
            var ciphertextLength = blob.Length - 1 - IvLength - MacLength;
            var macInput = blob.AsSpan(0, 1 + IvLength + ciphertextLength);
            var storedMac = blob.AsSpan(1 + IvLength + ciphertextLength, MacLength);

            // Verify BEFORE decrypting (correct Encrypt-then-MAC), in constant time.
            var expectedMac = HMACSHA256.HashData(macKey, macInput);
            if (!CryptographicOperations.FixedTimeEquals(expectedMac, storedMac))
                throw new CryptographicException("Token authentication failed (wrong machine/user, or tampered).");

            var iv = blob.AsSpan(1, IvLength).ToArray();
            var ciphertext = blob.AsSpan(1 + IvLength, ciphertextLength).ToArray();
            using var aes = Aes.Create();
            aes.Key = encKey;
            return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private (byte[] EncKey, byte[] MacKey) DeriveKeys()
    {
        // The IKM (machine id + username + fixed app salt) is low-secrecy binding material, not a
        // secret — machine ids are world-readable and the salt is a constant in the binary. It is
        // owned by the provider (which may hand back a cached/shared buffer), so we must NOT mutate
        // or zero it. The secrecy lives in the derived keys below, which the callers do zero.
        var ikm = _keyProvider.GetKeyMaterial();
        var encKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, s_hkdfSalt, s_infoEnc);
        var macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, s_hkdfSalt, s_infoMac);
        return (encKey, macKey);
    }
}
