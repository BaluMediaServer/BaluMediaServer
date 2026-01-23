# BaluMediaServer Performance Analysis Report

## 1. Executive Summary

This report details the findings of a performance analysis of the BaluMediaServer project. The primary goal was to identify the root causes of slow streaming performance. The analysis reveals several key bottlenecks, primarily related to inefficient CPU usage from polling, synchronous blocking operations, and suboptimal data handling. The most significant issues are the use of busy-wait loops for frame processing and on-the-fly JPEG encoding within the main streaming thread for RTSP clients.

The following sections provide a detailed breakdown of each issue and offer concrete suggestions for improvement.

## 2. Analysis Details & Suggestions

### 2.1. Inefficient Polling in Streaming Loops

**Finding:** The core streaming logic for H.264 clients relies on polling with `Task.Delay` rather than an event-driven, asynchronous approach. This happens in two key places: waiting for the first frame to start the encoder and waiting for subsequent encoded frames in the main streaming loop.

*   **File:** `RTSP/Streaming/StreamingController.cs`

**Code Snippet (Waiting for H.264 Frames):**
```csharp
if (client.Codec == CodecType.H264)
{
    frameSent = await StreamH264ToClientAsync(client, cancellationToken).ConfigureAwait(false);
    if (!frameSent)
    {
        // This Task.Delay creates a busy-wait loop
        await Task.Delay(H264PollIntervalMs, cancellationToken).ConfigureAwait(false);
    }
}
```

**Code Snippet (Waiting for First Frame):**
```csharp
while ((frame = GetLatestFrame?.Invoke(client.CameraId)) == null || frame.Data == null)
{
    // This poll adds latency to stream startup
    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
}
```

**Impact:**
*   **High CPU Usage:** The constant cycle of waking up, checking for a frame, and sleeping keeps the CPU busy and consumes resources unnecessarily.
*   **Increased Latency:** This method introduces artificial latency. A frame that arrives right after a check must wait for the full `Task.Delay` duration before it can be processed.

**Suggestion:**
Refactor the frame delivery mechanism to be event-driven.
*   For the H.264 stream, instead of polling `_encoderManager.TryDequeueFrame`, change it to an asynchronous method like `DequeueFrameAsync` that waits on a `Channel.Reader.ReadAsync` or a similar synchronization primitive. This allows the streaming thread to sleep efficiently until a frame is actually available.
*   For the first frame, use a `TaskCompletionSource` or `AsyncManualResetEvent` that is signaled by the camera service once the first frame is captured.

---

### 2.2. Synchronous, On-the-Fly JPEG Encoding for RTSP-MJPEG

**Finding:** For clients that connect via RTSP and request the `MJPEG` codec, the `StreamingController` encodes each YUV frame into JPEG format synchronously within that client's streaming loop.

*   **File:** `RTSP/Streaming/StreamingController.cs`
*   **Method:** `StreamMjpegToClientAsync` -> `EncodeToJpeg`

**Code Snippet:**
```csharp
private async Task StreamMjpegToClientAsync(Models.Client client, CancellationToken cancellationToken)
{
    var frame = GetLatestFrame?.Invoke(client.CameraId);
    if (frame != null && frame.Data != null)
    {
        // This encoding is CPU-intensive and blocks the loop
        var jpegData = EncodeToJpeg(frame.Data, frame.Width, frame.Height, ...);
        if (jpegData != null)
        {
            await _rtpBuilder.SendJpegAsRtpAsync(client, jpegData).ConfigureAwait(false);
        }
    }
}
```

**Impact:**
*   **Major Performance Bottleneck:** `YuvImage.CompressToJpeg` is a very CPU-intensive operation. Performing this work on the streaming thread directly impacts the frame rate and responsiveness of the stream.
*   **Duplicated Work:** If multiple clients request the same MJPEG stream, this expensive encoding work is duplicated for each client, scaling poorly.
*   **Low Frame Rate:** The streaming loop cannot process new frames until the current one is fully encoded and sent, leading to severe frame drops and slow video.

**Suggestion:**
Decouple JPEG encoding from the client streaming loop. The existing `MjpegServer` already implements a good pattern for this:
*   Create a single, background encoding task per camera (e.g., `FrontJpeqEncoderLoop`, `BackJpeqEncoderLoop`).
*   This task should read raw frames from a `System.Threading.Channels.Channel`, encode them to JPEG, and then push the JPEG data to another channel.
*   The `StreamMjpegToClientAsync` method should then become a simple consumer that reads ready-made JPEG frames from the output channel and sends them to the client. This offloads the heavy work from the client loop and encodes each frame only once.

---

### 2.3. Frame Data Copying from Native to Managed Code

**Finding:** In the `FrontCameraService` and `BackCameraService`, the `frame.GetData()` call copies video frame data from the native Android (Java/Kotlin) memory, managed by the `camerastreamer-release.aar` library, into a new `byte[]` array in the .NET managed heap. This occurs for every single frame captured by the camera.

*   **File:** `Services/FrontCameraService.cs`, `Services/BackCameraService.cs`
*   **Method:** `ProcessFrame`

**Impact:**
*   **Garbage Collector (GC) Pressure:** High-resolution video frames are large (e.g., >1MB for 720p). Allocating this much memory 30-45 times per second puts significant pressure on the .NET GC, which can lead to pauses, stutters, and overall reduced performance as the GC struggles to clean up the memory.
*   **JNI Overhead:** The act of copying data across the Java Native Interface (JNI) boundary has its own performance cost.

**Suggestion:**
This is a more advanced optimization, but it can yield significant benefits.
*   **Investigate Zero-Copy Techniques:** Explore if the underlying native library can write frame data directly into a pre-allocated `ByteBuffer` or `Memory<byte>` that is owned by the .NET runtime. This would avoid the copy entirely.
*   **Use `ArrayPool<byte>`:** If zero-copy is not possible, use `ArrayPool<byte>.Shared.Rent` to get buffers for the frame data instead of allocating a new `byte[]` every time. This reuses memory and dramatically reduces GC pressure. The `VideoFrame` class from the native binding and the `FrameEventArgs` would need to be adapted to work with pooled buffers.

---

### 2.4. Inefficient H.264 Encoder Frame Queue

**Finding:** The `H264Encoder` class implements its own frame queuing mechanism using a `ConcurrentQueue<FrameData>` and manually checks `_frameQueue.Count` to decide when to drop frames.

*   **File:** `Services/H264Encoder.cs`
*   **Method:** `QueueFrame`

**Code Snippet:**
```csharp
while (_frameQueue.Count > 2)
{
    if (_frameQueue.TryDequeue(out _))
    {
        Log.Debug("H264", "Dropped old frame to prevent latency");
    }
}
_frameQueue.Enqueue(new() { Data = frameData, Timestamp = timestamp });
```

**Impact:**
*   **Slightly Inefficient:** While functional, this pattern is less efficient than using a dedicated, bounded collection. `ConcurrentQueue.Count` is not always performant, and the `while` loop to drain the queue is extra work.
*   **Less Robust:** This implementation is more complex to get right compared to a purpose-built solution.

**Suggestion:**
Replace the `ConcurrentQueue` with a `System.Threading.Channels.Channel<FrameData>`. This is the same component used effectively in the camera services.
*   Initialize the channel with `Channel.CreateBounded<FrameData>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest })`.
*   The `QueueFrame` method becomes a single, non-blocking `_channel.Writer.TryWrite(frame)`.
*   The `EncodingLoop` would then use the highly efficient `await _channel.Reader.ReadAsync()` to wait for frames. This would also eliminate the `Thread.Sleep(1)` in the encoding loop, making it more efficient.
