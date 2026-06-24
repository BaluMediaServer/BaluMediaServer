using BaluMediaServer.Services;
using FluentAssertions;
using Xunit;

namespace BaluMediaServer.Tests.Unit.Services;

/// <summary>
/// Unit tests for the numeric kernels behind the software frame stabilizer:
/// temporal denoise behaviour, integral-projection motion estimation (including the
/// sign convention the stabilizer relies on), and the NV21 border-replicated shift.
/// </summary>
public class FrameStabilizerMathTests
{
    // ── temporal denoise ────────────────────────────────────────────────────────

    [Fact]
    public void Denoise_StaticSubThresholdNoise_ReducesDeviation()
    {
        const int n = 4096;
        const byte baseVal = 128;
        var prev = new byte[n];
        var data = new byte[n];
        var rng = new Random(123);
        for (int i = 0; i < n; i++)
        {
            prev[i] = baseVal;                         // clean history
            data[i] = (byte)(baseVal + rng.Next(-5, 6)); // noise well under the threshold
        }

        double before = MeanAbsDev(data, baseVal);
        FrameStabilizerMath.Denoise(data, prev, n, thr: 12, alphaQ7: 64); // 50% blend
        double after = MeanAbsDev(data, baseVal);

        after.Should().BeLessThan(before, "static pixels are blended toward the clean history");
    }

    [Fact]
    public void Denoise_LargeDelta_PassesThroughWithoutGhosting()
    {
        const int n = 256;
        var prev = new byte[n];
        var data = new byte[n];
        Array.Fill(prev, (byte)50);
        Array.Fill(data, (byte)200);   // delta 150 ≫ threshold → motion

        FrameStabilizerMath.Denoise(data, prev, n, thr: 12, alphaQ7: 64);

        data.Should().OnlyContain(b => b == 200, "moving content must pass through unblended");
        prev.Should().OnlyContain(b => b == 200, "history is updated with the output (recursive filter)");
    }

    // ── motion estimation ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3)]
    [InlineData(-4)]
    [InlineData(0)]
    public void BestShift1D_RecoversKnownTranslation(int contentShift)
    {
        const int n = 64;
        var prev = new int[n];
        var cur = new int[n];
        for (int i = 0; i < n; i++)
            prev[i] = (i * i) % 200 + i;               // distinctive, non-degenerate pattern

        // Content moved right by `contentShift`: cur[i] = prev[i - contentShift] (border-clamped).
        for (int i = 0; i < n; i++)
            cur[i] = prev[FrameStabilizerMath.Clamp(i - contentShift, 0, n - 1)];

        int d = FrameStabilizerMath.BestShift1D(cur, prev, n, maxShift: 8);

        // Optimum is at cur[i] ≈ prev[i+d] ⇒ d = -contentShift; the stabilizer reads
        // measured motion as -d, recovering the true content shift.
        d.Should().Be(-contentShift);
        (-d).Should().Be(contentShift);
    }

    [Fact]
    public void ComputeProjections_SumsRowsAndColumns()
    {
        // 3x2 image, values 1..6 row-major.
        var img = new byte[] { 1, 2, 3, 4, 5, 6 };
        var col = new int[3];
        var row = new int[2];

        FrameStabilizerMath.ComputeProjections(img, col, row, dw: 3, dh: 2);

        col.Should().Equal(1 + 4, 2 + 5, 3 + 6); // 5, 7, 9
        row.Should().Equal(1 + 2 + 3, 4 + 5 + 6); // 6, 15
    }

    // ── NV21 shift ────────────────────────────────────────────────────────────────

    [Fact]
    public void ShiftNv21_TranslatesLumaPixel()
    {
        const int w = 8, h = 8;
        int total = w * h + (w * h / 2);
        var src = new byte[total];
        var dst = new byte[total];
        src[3 * w + 3] = 255;   // bright luma pixel at (3,3)

        FrameStabilizerMath.ShiftNv21(src, dst, w, h, sx: 2, sy: 1);

        // output(x,y) = src(x-sx, y-sy) ⇒ the pixel lands at (5,4).
        dst[4 * w + 5].Should().Be(255);
        dst[3 * w + 3].Should().Be(0);
    }

    [Fact]
    public void ShiftNv21_ZeroShift_IsIdentity()
    {
        const int w = 8, h = 8;
        int total = w * h + (w * h / 2);
        var src = new byte[total];
        var dst = new byte[total];
        var rng = new Random(7);
        rng.NextBytes(src);

        FrameStabilizerMath.ShiftNv21(src, dst, w, h, sx: 0, sy: 0);

        dst.Should().Equal(src);
    }

    [Fact]
    public void ShiftNv21_ReplicatesBorderInsteadOfWrapping()
    {
        const int w = 8, h = 8;
        int total = w * h + (w * h / 2);
        var src = new byte[total];
        var dst = new byte[total];
        // Distinct left edge column so wrap-around would be detectable.
        for (int y = 0; y < h; y++)
            src[y * w + 0] = 111;

        FrameStabilizerMath.ShiftNv21(src, dst, w, h, sx: 3, sy: 0);

        // Columns 0..3 should all be the replicated left edge (111), not wrapped data.
        for (int x = 0; x <= 3; x++)
            dst[2 * w + x].Should().Be(111);
    }

    private static double MeanAbsDev(byte[] data, byte center)
    {
        long sum = 0;
        foreach (var b in data) sum += Math.Abs(b - center);
        return (double)sum / data.Length;
    }
}
