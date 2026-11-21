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

    // ---- Deduplicator: full-resolution comparison ----

    private const int W = 200, H = 100;

    /// <summary>A dark editor page with light "text" lines, as BGRA.</summary>
    private static byte[] Page(Func<int, int, bool>? extraText = null) =>
        Image(W, H, (x, y) => (y % 20 < 8 && x > 10 && x < 150) || (extraText?.Invoke(x, y) ?? false) ? (byte)220 : (byte)30);

    private static byte[] Gray(byte[] bgra) => Deduplicator.Grayscale(bgra, W, H);

    private static Deduplicator WithSaved(byte[] bgra, string title = "t")
    {
        var dedupe = new Deduplicator();
        dedupe.Remember(1, title, W, H, Gray(bgra), Today, imageId: 42);
        return dedupe;
    }

    [Fact]
    public void Nothing_saved_yet_is_never_a_duplicate()
    {
        Assert.Null(new Deduplicator().FindDuplicate(1, "t", W, H, Gray(Page()), Today));
    }

    [Fact]
    public void The_same_picture_is_a_duplicate_of_the_last_saved_image()
    {
        Assert.Equal(42, WithSaved(Page()).FindDuplicate(1, "t", W, H, Gray(Page()), Today));
    }

    [Fact]
    public void A_typed_word_is_a_change()
    {
        // A 12x6 block of new "text" in an empty area: far too small for a coarse hash.
        byte[] typed = Page((x, y) => x >= 160 && x < 172 && y >= 90 && y < 96);

        // The old check (dHash distance up to 4) called this a duplicate.
        Assert.True(DHash.HammingDistance(DHash.Compute(Page(), W, H), DHash.Compute(typed, W, H)) <= 4);
        Assert.Null(WithSaved(Page()).FindDuplicate(1, "t", W, H, Gray(typed), Today));
    }

    [Fact]
    public void A_blinking_cursor_and_slight_noise_are_not_a_change()
    {
        // A 2-pixel wide cursor bar, and every pixel a little brighter.
        byte[] cursor = Image(W, H, (x, y) =>
            x is 180 or 181 && y >= 60 && y < 78 ? (byte)220
            : (byte)(((y % 20 < 8 && x > 10 && x < 150) ? 220 : 30) + 10));

        Assert.Equal(42, WithSaved(Page()).FindDuplicate(1, "t", W, H, Gray(cursor), Today));
    }

    [Fact]
    public void Images_are_only_compared_within_the_same_window_size_and_day()
    {
        Deduplicator dedupe = WithSaved(Page());

        Assert.Null(dedupe.FindDuplicate(2, "t", W, H, Gray(Page()), Today));
        Assert.Null(dedupe.FindDuplicate(1, "t", W, H, Gray(Page()), Today.AddDays(1)));
        Assert.Null(dedupe.FindDuplicate(1, "t", W + 1, H, new byte[(W + 1) * H], Today));
    }

    [Fact]
    public void Comparison_is_against_the_last_saved_image_not_the_last_seen_one()
    {
        Deduplicator dedupe = WithSaved(Page());

        // Small steps that each stay below the tile threshold still add up against the saved image.
        byte[] step1 = Page((x, y) => x >= 160 && x < 163 && y >= 90 && y < 96);
        byte[] step2 = Page((x, y) => x >= 160 && x < 172 && y >= 90 && y < 96);
        Assert.Equal(42, dedupe.FindDuplicate(1, "t", W, H, Gray(step1), Today));
        Assert.Null(dedupe.FindDuplicate(1, "t", W, H, Gray(step2), Today));
    }

    [Fact]
    public void A_changed_title_is_never_a_duplicate()
    {
        Deduplicator dedupe = WithSaved(Page(), "id_ed25519.pub - Notepad++");

        // Same window, same picture, but another tab.
        Assert.Null(dedupe.FindDuplicate(1, "new 56 - Notepad++", W, H, Gray(Page()), Today));
        Assert.Equal(42, dedupe.FindDuplicate(1, "id_ed25519.pub - Notepad++", W, H, Gray(Page()), Today));
    }
}
