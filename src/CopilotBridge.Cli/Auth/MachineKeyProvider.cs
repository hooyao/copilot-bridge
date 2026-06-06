using System.Diagnostics;
using System.Text;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Default <see cref="IMachineKeyProvider"/>: assembles machine+user-bound key material from
/// OS-native sources, without any P/Invoke.
/// <list type="bullet">
///   <item><b>Linux</b>: <c>/etc/machine-id</c> (fallback <c>/var/lib/dbus/machine-id</c>).</item>
///   <item><b>macOS</b>: <c>IOPlatformUUID</c>, parsed from <c>ioreg -rd1 -c IOPlatformExpertDevice</c>.</item>
/// </list>
/// The material is <c>machineId || 0x1F || userName || 0x1F || appSalt</c>. The unit separator (0x1F)
/// makes the concatenation unambiguous; <see cref="Environment.UserName"/> mirrors DPAPI's per-user
/// binding; the fixed app salt domain-separates us from any other app deriving from the same machine id.
/// <para>
/// Only ever constructed on non-Windows platforms (see <see cref="TokenStore"/>). If no machine
/// identity can be read, <see cref="GetKeyMaterial"/> throws so the protector fails closed.
/// </para>
/// </summary>
internal sealed class MachineKeyProvider : IMachineKeyProvider
{
    private const byte Separator = 0x1F; // ASCII unit separator
    private static readonly byte[] s_appSalt = "copilot-bridge.machinekey.v1"u8.ToArray();

    private byte[]? _cached;

    public byte[] GetKeyMaterial()
    {
        // Cache: the provider is a long-lived singleton and the material is stable for the process
        // lifetime, so probe the OS (read /etc/machine-id, or spawn `ioreg`) only once rather than on
        // every token read/write. The material is low-secrecy binding data, not a key, so holding it
        // in memory is fine.
        if (_cached is not null) return _cached;

        var machineId = ReadMachineId();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            throw new InvalidOperationException(
                "Could not determine a stable machine identity for token encryption " +
                "(no /etc/machine-id, /var/lib/dbus/machine-id, or macOS IOPlatformUUID).");
        }

        var user = Environment.UserName ?? string.Empty;

        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes(machineId.Trim()));
        ms.WriteByte(Separator);
        ms.Write(Encoding.UTF8.GetBytes(user));
        ms.WriteByte(Separator);
        ms.Write(s_appSalt);
        _cached = ms.ToArray();
        return _cached;
    }

    private static string? ReadMachineId()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ReadMacOsPlatformUuid();
        }

        // Linux and other systemd/dbus unixes.
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(path))
                {
                    var id = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch
            {
                // Unreadable — try the next source.
            }
        }

        return null;
    }

    private static string? ReadMacOsPlatformUuid()
    {
        try
        {
            var psi = new ProcessStartInfo("ioreg", "-rd1 -c IOPlatformExpertDevice")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return ParseIOPlatformUUID(stdout);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the <c>IOPlatformUUID</c> value from <c>ioreg -rd1 -c IOPlatformExpertDevice</c> output.
    /// The line looks like: <c>    "IOPlatformUUID" = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"</c>.
    /// Extracted as a pure static so the parsing is unit-testable on Windows from a captured sample
    /// (the <c>ioreg</c> exec itself is macOS-only).
    /// </summary>
    internal static string? ParseIOPlatformUUID(string ioregOutput)
    {
        if (string.IsNullOrEmpty(ioregOutput)) return null;

        const string key = "\"IOPlatformUUID\"";
        var keyIdx = ioregOutput.IndexOf(key, StringComparison.Ordinal);
        if (keyIdx < 0) return null;

        // Find the opening quote of the value, after the '=' that follows the key.
        var eqIdx = ioregOutput.IndexOf('=', keyIdx + key.Length);
        if (eqIdx < 0) return null;

        var firstQuote = ioregOutput.IndexOf('"', eqIdx);
        if (firstQuote < 0) return null;

        var secondQuote = ioregOutput.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0) return null;

        var value = ioregOutput[(firstQuote + 1)..secondQuote].Trim();
        return value.Length == 0 ? null : value;
    }
}
