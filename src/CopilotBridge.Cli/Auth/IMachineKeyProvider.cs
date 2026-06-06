namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Supplies the stable, machine- and user-bound key material that
/// <see cref="DerivedKeyTokenProtector"/> runs through HKDF to derive its encryption and MAC keys.
/// <para>
/// Abstracted as an interface so the protector can be unit-tested on any OS by injecting a stub
/// that returns fixed bytes — the real <see cref="MachineKeyProvider"/> probes OS-specific sources
/// (<c>/etc/machine-id</c>, macOS <c>IOPlatformUUID</c>) that only exist on the target platform.
/// </para>
/// </summary>
internal interface IMachineKeyProvider
{
    /// <summary>
    /// Returns the raw input keying material: a stable composite of the machine identity, the current
    /// user, and a fixed app salt. Throws if no machine identity can be established (so the protector
    /// fails closed rather than silently deriving a weak/empty key).
    /// </summary>
    byte[] GetKeyMaterial();
}
