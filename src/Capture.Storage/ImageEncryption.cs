using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Capture.Storage;

/// <summary>
/// The file format of an encrypted image: "OTS1" | 12-byte nonce | ciphertext | 16-byte tag,
/// AES-256-GCM. The image's path relative to the storage root is the associated data, so a file
/// that was moved, renamed or swapped with another fails to decrypt.
/// </summary>
public static class ImageCipher
{
    public const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "OTS1"u8.ToArray();

    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext, string relativePath)
    {
        byte[] file = new byte[Magic.Length + NonceSize + plaintext.Length + TagSize];
        Span<byte> span = file;
        Magic.CopyTo(span);
        Span<byte> nonce = span.Slice(Magic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, span.Slice(Magic.Length + NonceSize, plaintext.Length),
            span[^TagSize..], AssociatedData(relativePath));
        return file;
    }

    /// <summary>Throws <see cref="CryptographicException"/> for a wrong key, a wrong path or any change to the file.</summary>
    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> file, string relativePath)
    {
        if (!IsEncrypted(file) || file.Length < Magic.Length + NonceSize + TagSize)
        {
            throw new CryptographicException("Not an encrypted image.");
        }
        ReadOnlySpan<byte> nonce = file.Slice(Magic.Length, NonceSize);
        ReadOnlySpan<byte> ciphertext = file[(Magic.Length + NonceSize)..^TagSize];
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, file[^TagSize..], plaintext, AssociatedData(relativePath));
        return plaintext;
    }

    public static bool IsEncrypted(ReadOnlySpan<byte> file) => file.StartsWith(Magic);

    /// <summary>Separators normalized, so the same file decrypts whichever way its path was written.</summary>
    private static byte[] AssociatedData(string relativePath) => Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/'));
}

/// <summary>Wraps and unwraps day keys, so that no key is ever stored in the clear.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] key);
    byte[] Unprotect(byte[] wrapped);
}

/// <summary>
/// Windows DPAPI, bound to the current user's login: only this Windows account on this machine
/// (or with its roaming profile) can unwrap the keys.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    // Ties the blobs to this app: another program calling DPAPI for the same user cannot unwrap
    // them without knowing this value too.
    // It still carries the project's old name (OpenTimesheet) on purpose: changing it would make
    // every key wrapped so far impossible to unwrap.
    private static readonly byte[] Entropy = "OpenTimesheet.TimeCapture.DayKey.v1"u8.ToArray();

    public byte[] Protect(byte[] key) => ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] wrapped) => ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// One random key per day, stored wrapped in the database (day_keys). Deleting a day's row makes
/// that day's images unrecoverable (crypto-shredding). Only the key of the day being written is
/// kept in memory; keys read for export are not cached.
/// </summary>
public sealed class DayKeyStore(CaptureDatabase database, IKeyProtector protector)
{
    private readonly Lock _lock = new();
    private DateOnly _cachedDay;
    private byte[]? _cachedKey;

    /// <summary>The key for writing images of <paramref name="day"/>, created on first use.</summary>
    public byte[] GetOrCreate(DateOnly day)
    {
        lock (_lock)
        {
            if (_cachedKey is not null && _cachedDay == day)
            {
                return _cachedKey;
            }

            byte[]? key = Find(day);
            if (key is null)
            {
                byte[] fresh = RandomNumberGenerator.GetBytes(ImageCipher.KeySize);
                byte[] wrapped = protector.Protect(fresh);
                Array.Clear(fresh);
                // Another process may have created the key in the meantime; whichever was stored first wins.
                key = protector.Unprotect(database.InsertDayKeyIfMissing(day, wrapped));
            }

            if (_cachedKey is not null)
            {
                Array.Clear(_cachedKey);
            }
            _cachedDay = day;
            _cachedKey = key;
            return key;
        }
    }

    /// <summary>The key of <paramref name="day"/>, or null if there is none (never created, or deleted by retention).</summary>
    public byte[]? Find(DateOnly day)
    {
        byte[]? wrapped = database.GetWrappedDayKey(day);
        return wrapped is null ? null : protector.Unprotect(wrapped);
    }
}
