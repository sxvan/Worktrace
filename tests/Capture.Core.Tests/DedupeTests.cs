using Capture.Core.Pipeline;

namespace Capture.Core.Tests;

public class DedupeTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);

    /// <summary>Grayscale image from a function of (x, y).</summary>
    private static byte[] Image(int width, int height, Func<int, int, byte> value)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = value(x, y);
                int i = (y * width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    [Fact]
    public void Identical_images_have_the_same_hash()
    {
        byte[] a = Image(640, 480, (x, y) => (byte)((x * 7 + y * 13) % 256));
        byte[] b = (byte[])a.Clone();

        Assert.Equal(DHash.Compute(a, 640, 480), DHash.Compute(b, 640, 480));
    }

    [Fact]
    public void Horizontal_gradients_in_opposite_directions_differ_in_every_bit()
    {
        ulong darkToLight = DHash.Compute(Image(900, 800, (x, _) => (byte)(x * 255 / 899)), 900, 800);
        ulong lightToDark = DHash.Compute(Image(900, 800, (x, _) => (byte)(255 - x * 255 / 899)), 900, 800);

        Assert.Equal(0UL, darkToLight);
        Assert.Equal(ulong.MaxValue, lightToDark);
        Assert.Equal(64, DHash.HammingDistance(darkToLight, lightToDark));
    }

    [Fact]
    public void A_large_change_moves_the_hash_far_and_a_tiny_one_does_not()
    {
        Func<int, int, byte> pattern = (x, y) => (byte)(((x / 100) * 37 + (y / 100) * 91) % 256);
        ulong original = DHash.Compute(Image(900, 800, pattern), 900, 800);

        // One changed pixel.
        ulong tiny = DHash.Compute(Image(900, 800, (x, y) => x == 5 && y == 5 ? (byte)255 : pattern(x, y)), 900, 800);
        // Left half replaced by a different pattern.
        ulong large = DHash.Compute(Image(900, 800, (x, y) => x < 450 ? (byte)((x / 50 * 53) % 256) : pattern(x, y)), 900, 800);

        Assert.Equal(0, DHash.HammingDistance(original, tiny));
        Assert.True(DHash.HammingDistance(original, large) > 4);
    }

    [Fact]
    public void Works_for_images_smaller_than_the_hash_grid()
    {
        DHash.Compute(Image(3, 2, (x, y) => (byte)(x + y)), 3, 2);
    }

    [Fact]
    public void Wrong_buffer_size_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => DHash.Compute(new byte[10], 4, 4));
    }

    [Fact]
    public void Nothing_saved_yet_is_never_a_duplicate()
    {
        Assert.Null(new Deduplicator(4).FindDuplicate(1, "t", 100, 100, 0xFF, Today));
    }

    [Fact]
    public void Hash_within_the_threshold_is_a_duplicate_of_the_last_saved_image()
    {
        var dedupe = new Deduplicator(4);
        dedupe.Remember(1, "t", 100, 100, 0b0000, Today, imageId: 42);

        Assert.Equal(42, dedupe.FindDuplicate(1, "t", 100, 100, 0b1111, Today));  // distance 4
        Assert.Null(dedupe.FindDuplicate(1, "t", 100, 100, 0b11111, Today));      // distance 5
    }

    [Fact]
    public void Images_are_only_compared_within_the_same_window_size_and_day()
    {
        var dedupe = new Deduplicator(4);
        dedupe.Remember(1, "t", 100, 100, 0, Today, imageId: 42);

        Assert.Null(dedupe.FindDuplicate(2, "t", 100, 100, 0, Today));
        Assert.Null(dedupe.FindDuplicate(1, "t", 120, 100, 0, Today));
        Assert.Null(dedupe.FindDuplicate(1, "t", 100, 100, 0, Today.AddDays(1)));
    }

    [Fact]
    public void Comparison_is_against_the_last_saved_image_not_the_last_seen_one()
    {
        var dedupe = new Deduplicator(4);
        dedupe.Remember(1, "t", 100, 100, 0b000000, Today, imageId: 1);

        // Drifts 3 bits at a time; each step is close to the previous one but not remembered.
        Assert.Equal(1, dedupe.FindDuplicate(1, "t", 100, 100, 0b000111, Today));
        Assert.Null(dedupe.FindDuplicate(1, "t", 100, 100, 0b111111, Today));
    }

    [Fact]
    public void A_changed_title_is_never_a_duplicate()
    {
        var dedupe = new Deduplicator(4);
        dedupe.Remember(1, "id_ed25519.pub - Notepad++", 100, 100, 0, Today, imageId: 42);

        // Same window, same picture as far as the hash can tell, but another tab.
        Assert.Null(dedupe.FindDuplicate(1, "new 56 - Notepad++", 100, 100, 0, Today));
        Assert.Equal(42, dedupe.FindDuplicate(1, "id_ed25519.pub - Notepad++", 100, 100, 0, Today));
    }
}
