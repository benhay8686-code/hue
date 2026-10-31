using System.Security.Cryptography;
using System.Text.Json;
using HueStudio.Core.Storage;

namespace HueStudio.App.Services;

/// <summary>Where Hue Studio keeps its files: %LOCALAPPDATA%\HueStudio.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HueStudio");

    public static string Bridges => Path.Combine(Root, "bridges.dat");
    public static string Settings => Path.Combine(Root, "settings.json");
}

/// <summary>Encrypts paired-bridge keys with Windows DPAPI, so only this Windows user can read them.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "HueStudio.v1"u8.ToArray();
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}

public sealed class AppSettings
{
    public bool DemoMode { get; set; }
    public string? ActiveBridgeId { get; set; }
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(AppPaths.Settings)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
