using System.Numerics;

namespace BaluMediaServer.Services;

/// <summary>
/// Dependency-free numeric kernels for <see cref="FrameStabilizer"/>: temporal denoise,
/// luma downscale, integral projections, 1-D projection alignment, and NV21 frame shift.
/// Kept separate from <see cref="FrameStabilizer"/> (which carries the Android logger and
/// reusable buffers) so the algorithms can be unit-tested on a plain .NET target.
/// </summary>
public static class FrameStabilizerMath
{
    // ── temporal denoise ────────────────────────────────────────────────────────

    /// <summary>
    /// Motion-adaptive temporal denoise of <paramref name="data"/> against <paramref name="prev"/>,
    /// in place over <c>[0, length)</c>. For each byte: if the absolute delta to the previous
    /// (denoised) value is below <paramref name="thr"/> the byte is blended toward the previous
    /// value by <paramref name="alphaQ7"/>/128 (static → smoothed); otherwise it passes through
    /// unchanged (motion → no ghosting). <paramref name="prev"/> is updated with the output
    /// (recursive filter) so it is ready to serve as history for the next frame.
    /// </summary>
    public static void Denoise(byte[] data, byte[] prev, int length, short thr, short alphaQ7)
    {
        int step = Vector<byte>.Count;
        var thrVec = new Vector<short>(thr);
        var aVec = new Vector<short>(alphaQ7);

        int i = 0;
        if (length >= step)
        {
            for (; i <= length - step; i += step)
            {
                var vc = new Vector<byte>(data, i);
                var vp = new Vector<byte>(prev, i);

                Vector.Widen(vc, out Vector<ushort> cLoU, out Vector<ushort> cHiU);
                Vector.Widen(vp, out Vector<ushort> pLoU, out Vector<ushort> pHiU);

                var rLo = BlendHalf(Vector.AsVectorInt16(cLoU), Vector.AsVectorInt16(pLoU), thrVec, aVec);
                var rHi = BlendHalf(Vector.AsVectorInt16(cHiU), Vector.AsVectorInt16(pHiU), thrVec, aVec);

                var outBytes = Vector.Narrow(Vector.AsVectorUInt16(rLo), Vector.AsVectorUInt16(rHi));
                outBytes.CopyTo(data, i);
                outBytes.CopyTo(prev, i);
            }
        }

        for (; i < length; i++)
        {
            int cur = data[i];
            int delta = prev[i] - cur;
            int outv = (Math.Abs(delta) < thr) ? cur + ((delta * alphaQ7) >> 7) : cur;
            byte b = (byte)(outv < 0 ? 0 : outv > 255 ? 255 : outv);
            data[i] = b;
            prev[i] = b;
        }
    }

    /// <summary>
    /// Blends one widened half-vector: <c>static ? cur + (prev-cur)*alpha : cur</c>.
    /// Inputs are 0..255 held in <see cref="Vector{Int16}"/>; the result stays in 0..255.
    /// </summary>
    private static Vector<short> BlendHalf(Vector<short> cur, Vector<short> prev, Vector<short> thr, Vector<short> aQ7)
    {
        var delta = prev - cur;
        var absd = Vector.Abs(delta);
        var staticMask = Vector.LessThan(absd, thr);                 // -1 where |delta| < thr
        var blended = cur + Vector.ShiftRightArithmetic(delta * aQ7, 7);
        return Vector.ConditionalSelect(staticMask, blended, cur);
    }

    // ── motion estimation ────────────────────────────────────────────────────────

    /// <summary>Subsamples the luma plane into <paramref name="downY"/> (nearest, stride <paramref name="scale"/>).</summary>
    public static void DownscaleLuma(byte[] data, byte[] downY, int w, int dw, int dh, int scale)
    {
        for (int y = 0; y < dh; y++)
        {
            int srcRow = (y * scale) * w;
            int dstRow = y * dw;
            for (int x = 0; x < dw; x++)
                downY[dstRow + x] = data[srcRow + x * scale];
        }
    }

    /// <summary>Integral projections (column sums and row sums) of a downscaled luma image.</summary>
    public static void ComputeProjections(byte[] downY, int[] colSum, int[] rowSum, int dw, int dh)
    {
        Array.Clear(colSum, 0, dw);
        for (int y = 0; y < dh; y++)
        {
            int b = y * dw;
            int rowAcc = 0;
            for (int x = 0; x < dw; x++)
            {
                int v = downY[b + x];
                colSum[x] += v;
                rowAcc += v;
            }
            rowSum[y] = rowAcc;
        }
    }

    /// <summary>
    /// Finds the integer offset <c>d</c> in <c>[-maxShift, maxShift]</c> that best aligns
    /// <paramref name="cur"/> to <paramref name="prev"/> (minimum mean absolute difference
    /// over the overlapping region — normalized so different overlap widths compare fairly).
    /// <c>cur[i] ≈ prev[i+d]</c> at the optimum, i.e. the content moved by <c>-d</c>.
    /// </summary>
    public static int BestShift1D(int[] cur, int[] prev, int n, int maxShift)
    {
        int bestD = 0;
        double bestCost = double.MaxValue;
        for (int d = -maxShift; d <= maxShift; d++)
        {
            long sum = 0;
            int count = 0;
            int start = Math.Max(0, -d);
            int end = Math.Min(n, n - d);
            for (int i = start; i < end; i++)
            {
                int diff = cur[i] - prev[i + d];
                sum += diff < 0 ? -diff : diff;
                count++;
            }
            if (count == 0) continue;
            double cost = (double)sum / count;
            if (cost < bestCost)
            {
                bestCost = cost;
                bestD = d;
            }
        }
        return bestD;
    }

    // ── frame shift ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies <paramref name="src"/> to <paramref name="dst"/> with content translated by
    /// (<paramref name="sx"/>, <paramref name="sy"/>) pixels; out-of-frame samples are
    /// border-replicated. Handles the NV21 luma plane and the VU-interleaved chroma plane
    /// (chroma shifted by half, since it is 2× subsampled).
    /// </summary>
    public static void ShiftNv21(byte[] src, byte[] dst, int w, int h, int sx, int sy)
    {
        int ySize = w * h;

        // Luma
        for (int y = 0; y < h; y++)
        {
            int srcY = Clamp(y - sy, 0, h - 1);
            int dstRow = y * w;
            int srcRow = srcY * w;
            for (int x = 0; x < w; x++)
            {
                int srcX = Clamp(x - sx, 0, w - 1);
                dst[dstRow + x] = src[srcRow + srcX];
            }
        }

        // Chroma (VU pairs): cw pairs per row of w bytes, ch rows.
        int cw = w / 2, ch = h / 2;
        int csx = sx / 2, csy = sy / 2;
        for (int cy = 0; cy < ch; cy++)
        {
            int srcCy = Clamp(cy - csy, 0, ch - 1);
            int dstRow = ySize + cy * w;
            int srcRow = ySize + srcCy * w;
            for (int cx = 0; cx < cw; cx++)
            {
                int srcCx = Clamp(cx - csx, 0, cw - 1);
                int d = dstRow + cx * 2;
                int s = srcRow + srcCx * 2;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
            }
        }
    }

    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
