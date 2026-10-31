using System.Text.Json;

namespace HueStudio.Core.Storage;

public sealed class BridgeCredentials
{
    public string BridgeId { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>The "hue-application-key" (v1 username) used for every CLIP v2 call.</summary>
    public string ApplicationKey { get; set; } = "";
    /// <summary>32-hex-char PSK for the Entertainment API DTLS stream.</summary>
    public string? ClientKey { get; set; }
    /// <summary>Pinned SHA-256 thumbprint of the bridge's TLS certificate.</summary>
    public string? CertificateSha256 { get; set; }
    public DateTimeOffset PairedAt { get; set; }
}

/// <summary>Encrypts secrets at rest. The Windows app supplies a DPAPI implementation.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>No-op protector for tests and non-Windows builds.</summary>
public sealed class PlainTextProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;
    public byte[] Unprotect(byte[] ciphertext) => ciphertext;
}

/// <summary>Stores paired bridges in a single (encrypted) file, e.g. %LOCALAPPDATA%\HueStudio\bridges.dat.</summary>
public sealed class CredentialStore(string path, ISecretProtector protector)
{
    public async Task<List<BridgeCredentials>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var bytes = protector.Unprotect(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
            return JsonSerializer.Deserialize<List<BridgeCredentials>>(bytes) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException)
        {
            return []; // corrupt or written by another Windows user: start fresh
        }
    }

    public async Task SaveAsync(IEnumerable<BridgeCredentials> bridges, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(bridges.ToList()));
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public async Task UpsertAsync(BridgeCredentials bridge, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        all.RemoveAll(b => string.Equals(b.BridgeId, bridge.BridgeId, StringComparison.OrdinalIgnoreCase));
        all.Insert(0, bridge);
        await SaveAsync(all, ct).ConfigureAwait(false);
    }
}
