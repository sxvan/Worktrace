using Capture.Core.Geometry;

namespace Capture.Core.Tests;

public class RegionTests
{
    private static long Area(IEnumerable<RectInt> rects) => rects.Sum(r => (long)r.Width * r.Height);

    private static void AssertDisjoint(IReadOnlyList<RectInt> rects)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                Assert.True(rects[i].Intersect(rects[j]).IsEmpty, $"{rects[i]} overlaps {rects[j]}");
            }
        }
    }

    [Fact]
    public void Nothing_covering_leaves_the_whole_rectangle()
    {
        var source = new RectInt(0, 0, 100, 50);

        Assert.Equal([source], Region.Subtract(source, []));
    }

    [Fact]
    public void Full_cover_leaves_nothing()
    {
        Assert.Empty(Region.Subtract(new RectInt(10, 10, 20, 20), [new RectInt(0, 0, 100, 100)]));
    }

    [Fact]
    public void Non_overlapping_cover_changes_nothing()
    {
        var source = new RectInt(0, 0, 100, 100);

        Assert.Equal([source], Region.Subtract(source, [new RectInt(100, 0, 200, 100)]));
    }

    [Fact]
    public void Cover_in_the_middle_leaves_four_pieces()
    {
        var source = new RectInt(0, 0, 100, 100);
        var cover = new RectInt(40, 40, 60, 60);

        var result = Region.Subtract(source, [cover]);

        Assert.Equal(4, result.Count);
        AssertDisjoint(result);
        Assert.Equal(100 * 100 - 20 * 20, Area(result));
        Assert.All(result, r => Assert.True(r.Intersect(cover).IsEmpty));
    }

    [Fact]
    public void Cover_over_the_right_half_leaves_the_left_half()
    {
        var result = Region.Subtract(new RectInt(0, 0, 100, 100), [new RectInt(50, -10, 300, 300)]);

        Assert.Equal([new RectInt(0, 0, 50, 100)], result);
    }

    [Fact]
    public void Overlapping_covers_are_not_subtracted_twice()
    {
        var source = new RectInt(0, 0, 100, 100);
        RectInt[] covers = [new(0, 0, 60, 60), new(40, 40, 100, 100)];

        var result = Region.Subtract(source, covers);

        AssertDisjoint(result);
        // Union of the covers: 3600 + 3600 - 400 overlap.
        Assert.Equal(100 * 100 - (3600 + 3600 - 400), Area(result));
        Assert.All(result, r => Assert.All(covers, c => Assert.True(r.Intersect(c).IsEmpty)));
    }

    [Fact]
    public void Works_with_negative_coordinates()
    {
        var source = new RectInt(-1920, -200, -920, 300);

        var result = Region.Subtract(source, [new RectInt(-1500, -300, -1000, 0)]);

        AssertDisjoint(result);
        Assert.Equal(1000L * 500 - 500 * 200, Area(result));
        Assert.All(result, r => Assert.True(source.Contains(r)));
    }

    [Fact]
    public void Empty_rectangles_are_ignored()
    {
        var source = new RectInt(0, 0, 10, 10);

        Assert.Empty(Region.Subtract(RectInt.Empty, [source]));
        Assert.Equal([source], Region.Subtract(source, [new RectInt(5, 5, 5, 9)]));
    }

    [Fact]
    public void Matches_a_brute_force_pixel_count_for_random_layouts()
    {
        var random = new Random(12345);
        for (int round = 0; round < 200; round++)
        {
            var source = RandomRect(random);
            var covers = Enumerable.Range(0, random.Next(0, 6)).Select(_ => RandomRect(random)).ToList();

            var result = Region.Subtract(source, covers);

            AssertDisjoint(result);
            long expected = 0;
            for (int x = source.Left; x < source.Right; x++)
            {
                for (int y = source.Top; y < source.Bottom; y++)
                {
                    var pixel = new RectInt(x, y, x + 1, y + 1);
                    if (!covers.Any(c => c.Contains(pixel)))
                    {
                        expected++;
                        Assert.Contains(result, r => r.Contains(pixel));
                    }
                }
            }
            Assert.Equal(expected, Area(result));
        }
    }

    private static RectInt RandomRect(Random random)
    {
        int left = random.Next(-30, 30), top = random.Next(-30, 30);
        return new RectInt(left, top, left + random.Next(1, 40), top + random.Next(1, 40));
    }
}
