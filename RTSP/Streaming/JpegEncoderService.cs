using System.Threading.Channels;
using Android.Util;
using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Streaming;

/// <summary>
/// Shared JPEG encoding service for both HTTP MJPEG and RTSP MJPEG streaming.
/// Encodes frames in background tasks to avoid blocking client streaming loops.
/// </summary>
public class JpegEncoderService : IDisposable
{
    private readonly Channel<FrameEventArgs> _backInputChannel;
    private readonly Channel<FrameEventArgs> _frontInputChannel;
    private readonly Channel<EncodedJpegFrame> _backOutputChannel;
    private readonly Channel<EncodedJpegFrame> _frontOutputChannel;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _backEncoderTask;
    private readonly Task _frontEncoderTask;

    private int _quality;
    private volatile bool _disposed;  // Prevents JNI access after disposal

    /// <summary>
    /// Gets the output channel for back camera JPEG frames.
    /// </summary>
    public ChannelReader<EncodedJpegFrame> BackCameraOutput => _backOutputChannel.Reader;

    /// <summary>
    /// Gets the output channel for front camera JPEG frames.
    /// </summary>
    public ChannelReader<EncodedJpegFrame> FrontCameraOutput => _frontOutputChannel.Reader;

    /// <summary>
    /// Initializes a new instance of the JPEG encoder service.
    /// </summary>
    /// <param name="quality">JPEG quality (1-100).</param>
    /// <param name="inputBufferSize">Size of input frame buffer.</param>
    /// <param name="outputBufferSize">Size of output JPEG buffer.</param>
    public JpegEncoderService(int quality = 80, int inputBufferSize = 2, int outputBufferSize = 5)
    {
        _quality = quality;

        // Input channels for raw frames (drop oldest to prevent latency)
        _backInputChannel = Channel.CreateBounded<FrameEventArgs>(
            new BoundedChannelOptions(inputBufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        _frontInputChannel = Channel.CreateBounded<FrameEventArgs>(
            new BoundedChannelOptions(inputBufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        // Output channels for encoded JPEG frames (larger buffer for multiple consumers)
        _backOutputChannel = Channel.CreateBounded<EncodedJpegFrame>(
            new BoundedChannelOptions(outputBufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true
            });

        _frontOutputChannel = Channel.CreateBounded<EncodedJpegFrame>(
            new BoundedChannelOptions(outputBufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true
            });

        // Start background encoding tasks
        _backEncoderTask = Task.Run(() => EncoderLoopAsync(_backInputChannel.Reader, _backOutputChannel.Writer, 0), _cts.Token);
        _frontEncoderTask = Task.Run(() => EncoderLoopAsync(_frontInputChannel.Reader, _frontOutputChannel.Writer, 1), _cts.Token);

        Log.Info("[JpegEncoderService]", $"Started with quality={quality}, inputBuffer={inputBufferSize}, outputBuffer={outputBufferSize}");
    }

    /// <summary>
    /// Queues a raw frame for JPEG encoding.
    /// </summary>
    /// <param name="frame">The raw frame data.</param>
    /// <param name="cameraId">Camera ID (0=back, 1=front).</param>
    public void QueueFrame(FrameEventArgs frame, int cameraId)
    {
        if (frame == null || frame.Data == null || frame.Data.Length == 0)
            return;

        var channel = cameraId == 1 ? _frontInputChannel : _backInputChannel;
        channel.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Updates the JPEG quality setting.
    /// </summary>
    /// <param name="quality">New quality value (1-100).</param>
    public void SetQuality(int quality)
    {
        _quality = Math.Clamp(quality, 1, 100);
        Log.Debug("[JpegEncoderService]", $"Quality updated to {_quality}");
    }

    /// <summary>
    /// Background encoding loop that reads raw frames and writes JPEG frames.
    /// Checks _disposed flag before JNI calls to prevent SIGSEGV on disposed Java objects.
    /// </summary>
    private async Task EncoderLoopAsync(ChannelReader<FrameEventArgs> input, ChannelWriter<EncodedJpegFrame> output, int cameraId)
    {
        var cameraName = cameraId == 1 ? "Front" : "Back";
        Log.Debug("[JpegEncoderService]", $"{cameraName} camera encoder loop started");

        try
        {
            await foreach (var frame in input.ReadAllAsync(_cts.Token))
            {
                // Check disposed flag BEFORE any JNI calls to prevent SIGSEGV
                if (_disposed)
                {
                    Log.Debug("[JpegEncoderService]", $"{cameraName} encoder loop exiting - service disposed");
                    break;
                }

                try
                {
                    // Validate frame data before JNI encoding
                    if (frame.Data == null || frame.Data.Length == 0 || frame.Width <= 0 || frame.Height <= 0)
                    {
                        continue;
                    }

                    var jpegData = EncodeToJpeg(frame.Data, frame.Width, frame.Height,
                        Android.Graphics.ImageFormatType.Nv21, _quality);

                    if (jpegData != null && jpegData.Length > 0 && !_disposed)
                    {
                        var encodedFrame = new EncodedJpegFrame
                        {
                            Data = jpegData,
                            Width = frame.Width,
                            Height = frame.Height,
                            Timestamp = DateTime.UtcNow
                        };

                        // Non-blocking write (will drop oldest if full)
                        output.TryWrite(encodedFrame);
                    }
                }
                catch (Exception ex)
                {
                    if (!_disposed) // Only log if not disposing
                    {
                        Log.Error("[JpegEncoderService]", $"{cameraName} encoding error: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[JpegEncoderService]", $"{cameraName} camera encoder loop cancelled");
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Log.Error("[JpegEncoderService]", $"{cameraName} camera encoder loop error: {ex.Message}");
            }
        }
        finally
        {
            output.Complete();
            Log.Debug("[JpegEncoderService]", $"{cameraName} camera encoder loop stopped");
        }
    }

    // Reusable MemoryStream per encoder thread to avoid repeated allocation.
    // Thread-safety: each encoder loop (back/front) calls EncodeToJpeg on its own thread,
    // and we use [ThreadStatic] to give each thread its own instance.
    [ThreadStatic]
    private static MemoryStream? t_outputStream;

    /// <summary>
    /// Encodes raw YUV frame data to JPEG format.
    /// Explicitly disposes Java objects to prevent SIGSEGV on background threads.
    /// Reuses a per-thread MemoryStream to reduce GC pressure.
    /// </summary>
    private static byte[] EncodeToJpeg(byte[] rawImageData, int width, int height,
        Android.Graphics.ImageFormatType format, int quality)
    {
        // Validate input to prevent JNI crashes
        if (rawImageData == null || rawImageData.Length == 0 || width <= 0 || height <= 0)
        {
            return Array.Empty<byte>();
        }

        Android.Graphics.YuvImage? yuvImage = null;
        Android.Graphics.Rect? rect = null;
        Android.Graphics.Bitmap? bitmap = null;

        try
        {
            var outputStream = t_outputStream ??= new MemoryStream(width * height); // pre-size
            outputStream.SetLength(0); // reset for reuse

            if (format == Android.Graphics.ImageFormatType.Nv21 ||
                format == Android.Graphics.ImageFormatType.Yuv420888)
            {
                yuvImage = new Android.Graphics.YuvImage(rawImageData,
                    Android.Graphics.ImageFormatType.Nv21, width, height, null);
                rect = new Android.Graphics.Rect(0, 0, width, height);
                yuvImage.CompressToJpeg(rect, quality, outputStream);
            }
            else
            {
                bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(rawImageData, 0, rawImageData.Length);
                if (bitmap != null)
                {
                    bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, quality, outputStream);
                }
                else
                {
                    Log.Error("[JpegEncoderService]", "Failed to decode image data");
                    return Array.Empty<byte>();
                }
            }

            // Use GetBuffer + length to avoid the extra copy from ToArray()
            var length = (int)outputStream.Length;
            var result = new byte[length];
            Buffer.BlockCopy(outputStream.GetBuffer(), 0, result, 0, length);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("[JpegEncoderService]", $"JPEG encoding error: {ex.Message}");
            return Array.Empty<byte>();
        }
        finally
        {
            // Explicitly dispose Java objects to prevent JNI crashes
            try { rect?.Dispose(); } catch { }
            try { yuvImage?.Dispose(); } catch { }
            try { bitmap?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Releases all resources used by the encoder service.
    /// Sets _disposed flag FIRST to stop encoder loops before any JNI cleanup.
    /// </summary>
    public void Dispose()
    {
        // Set disposed flag FIRST to stop encoder loops from accessing Java objects
        _disposed = true;

        _cts?.Cancel();

        _backInputChannel.Writer.TryComplete();
        _frontInputChannel.Writer.TryComplete();

        try
        {
            // Wait for encoder tasks to complete - give them time to exit cleanly
            var tasks = new[] { _backEncoderTask, _frontEncoderTask };
            if (!Task.WaitAll(tasks, TimeSpan.FromSeconds(5)))
            {
                Log.Warn("[JpegEncoderService]", "Encoder tasks did not complete within 5s timeout");
            }
        }
        catch (AggregateException ex)
        {
            Log.Warn("[JpegEncoderService]", $"Encoder task errors during dispose: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Warn("[JpegEncoderService]", $"Dispose error: {ex.Message}");
        }

        _cts?.Dispose();
        Log.Info("[JpegEncoderService]", "Disposed");
    }
}

/// <summary>
/// Represents an encoded JPEG frame.
/// </summary>
public class EncodedJpegFrame
{
    /// <summary>
    /// Gets or sets the JPEG encoded data.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Gets or sets the frame width.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the frame height.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the encoding timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }
}
