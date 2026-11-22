using System.Security.Cryptography;
using Capture.Core.Redaction;
using Capture.Storage;
using Microsoft.Data.Sqlite;

namespace Capture.Core.Tests;

public sealed class ImageEncryptionTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 11, 21);
    private static readonly byte[] Png = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ots-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CaptureDatabase _database;

    public ImageEncryptionTests()
    {
        Directory.CreateDirectory(_root);
        _database = new CaptureDatabase(Path.Combine(_root, "capture.db"), new TextScrubber([]));
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    /// <summary>Stands in for DPAPI, which is per user and machine: reversible, but the blob is not the key.</summary>
    private sealed class XorProtector : IKeyProtector
    {
        public byte[] Protect(byte[] key) => [.. key.Select(b => (byte)(b ^ 0x5A)), 0xEE];
        public byte[] Unprotect(byte[] wrapped) => wrapped[..^1].Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    private FileImageStore Store() => new(_root, 0, new DayKeyStore(_database, new XorProtector()));

    private static string PathFor(DateOnly day, string name) => Path.Combine("captures", day.ToString("yyyy-MM-dd"), name);

    [Fact]
    public void Cipher_round_trip()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] file = ImageCipher.Encrypt(key, Png, @"captures\2026-11-21\a.png.enc");

        Assert.True(ImageCipher.IsEncrypted(file));
        Assert.Equal(Png.Length + 4 + 12 + 16, file.Length);
        Assert.Equal(Png, ImageCipher.Decrypt(key, file, @"captures\2026-11-21\a.png.enc"));
        // Separators do not matter.
        Assert.Equal(Png, ImageCipher.Decrypt(key, file, "captures/2026-11-21/a.png.enc"));
    }

    [Fact]
    public void Same_image_encrypts_differently_each_time()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(ImageCipher.Encrypt(key, Png, "p"), ImageCipher.Encrypt(key, Png, "p"));
    }

    [Fact]
    public void Tampered_file_fails()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] file = ImageCipher.Encrypt(key, Png, "p");
        file[100] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ImageCipher.Decrypt(key, file, "p"));
    }

    [Fact]
    public void Wrong_path_or_key_fails()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] file = ImageCipher.Encrypt(key, Png, @"captures\2026-11-21\a.png.enc");
        Assert.ThrowsAny<CryptographicException>(() => ImageCipher.Decrypt(key, file, @"captures\2026-11-21\b.png.enc"));
        Assert.ThrowsAny<CryptographicException>(() => ImageCipher.Decrypt(RandomNumberGenerator.GetBytes(32), file, @"captures\2026-11-21\a.png.enc"));
    }

    [Fact]
    public void Day_key_is_created_once_and_stored_wrapped()
    {
        var keys = new DayKeyStore(_database, new XorProtector());
        byte[] first = (byte[])keys.GetOrCreate(Day).Clone();

        Assert.Equal(first, keys.GetOrCreate(Day));
        // A second store (as after a restart) finds the same key.
        Assert.Equal(first, new DayKeyStore(_database, new XorProtector()).Find(Day));
        Assert.NotEqual(first, _database.GetWrappedDayKey(Day));
        Assert.NotEqual(first, keys.GetOrCreate(Day.AddDays(1)));
        Assert.Null(keys.Find(Day.AddDays(-1)));
    }

    [Fact]
    public void Store_writes_ciphertext_and_reads_the_image_back()
    {
        FileImageStore store = Store();
        string path = PathFor(Day, "120000_000_w1_app.png.enc");
        store.Write(path, Day, (byte[])Png.Clone());

        byte[] onDisk = File.ReadAllBytes(Path.Combine(_root, path));
        Assert.True(ImageCipher.IsEncrypted(onDisk));
        Assert.Equal(Png, store.ReadUnredacted(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "captures", "2026-11-21"), "*.tmp"));
    }

    [Fact]
    public void Swapped_files_fail()
    {
        FileImageStore store = Store();
        string a = PathFor(Day, "a.png.enc"), b = PathFor(Day, "b.png.enc");
        store.Write(a, Day, (byte[])Png.Clone());
        store.Write(b, Day, (byte[])Png.Clone());
        File.Copy(Path.Combine(_root, a), Path.Combine(_root, b), overwrite: true);

        Assert.ThrowsAny<CryptographicException>(() => store.ReadUnredacted(b));
    }

    [Fact]
    public void Missing_day_key_fails()
    {
        FileImageStore store = Store();
        string path = PathFor(Day, "a.png.enc");
        store.Write(path, Day, (byte[])Png.Clone());
        _database.DeleteDayKeysBefore(Day.AddDays(1));

        Assert.ThrowsAny<CryptographicException>(() => Store().ReadUnredacted(path));
    }

    [Fact]
    public void Unencrypted_files_are_read_as_they_are()
    {
        string path = PathFor(Day, "a.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, path))!);
        File.WriteAllBytes(Path.Combine(_root, path), Png);

        Assert.Equal(Png, Store().ReadUnredacted(path));
        Assert.Equal(Png, new FileImageStore(_root, 0).ReadUnredacted(path));
    }

    [Fact]
    public void Retention_deletes_day_keys_and_folders()
    {
        FileImageStore store = Store();
        DateOnly old = Day.AddDays(-20);
        store.Write(PathFor(old, "a.png.enc"), old, (byte[])Png.Clone());
        store.Write(PathFor(Day, "b.png.enc"), Day, (byte[])Png.Clone());

        new RetentionCleaner(_root, _database, retentionDays: 14).Run(Day);

        Assert.Null(_database.GetWrappedDayKey(old));
        Assert.NotNull(_database.GetWrappedDayKey(Day));
        Assert.False(Directory.Exists(Path.Combine(_root, "captures", old.ToString("yyyy-MM-dd"))));
        Assert.Equal(Png, store.ReadUnredacted(PathFor(Day, "b.png.enc")));
    }

    [Fact]
    public void Export_day_writes_decrypted_images()
    {
        FileImageStore store = Store();
        store.Write(PathFor(Day, "a.png.enc"), Day, (byte[])Png.Clone());
        string target = Path.Combine(_root, "export");

        (int exported, int failed) = store.ExportDay(Day, target);

        Assert.Equal((1, 0), (exported, failed));
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(target, "a.png")));
        Assert.Equal([Day], store.Days());
    }

    [Fact]
    public void Database_of_an_earlier_version_is_refused()
    {
        string old = Path.Combine(_root, "old.db");
        using (var connection = new SqliteConnection($"Data Source={old};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE capture_text (id INTEGER PRIMARY KEY); PRAGMA user_version=5;";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<InvalidDataException>(() => new CaptureDatabase(old, new TextScrubber([])));
        Assert.Contains("delete it", error.Message);
    }

    [Fact]
    public void New_database_has_no_text_table()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "capture.db")};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'capture_text';";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal((long)CaptureDatabase.SchemaVersion, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Dpapi_protector_round_trip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var protector = new DpapiKeyProtector();
        byte[] wrapped = protector.Protect(key);

        Assert.NotEqual(key, wrapped);
        Assert.Equal(key, protector.Unprotect(wrapped));
    }
}
