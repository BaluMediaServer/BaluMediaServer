# 📡 Balu Media Server - MAUI RTSP Server for Android

[![MIT License](https://img.shields.io/badge/License-MIT-green.svg)](https://choosealicense.com/licenses/mit/)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Android](https://img.shields.io/badge/Android-8.0%2B-green.svg)](https://developer.android.com/)
[![Platform](https://img.shields.io/badge/Platform-Android-brightgreen.svg)](https://developer.android.com/)

A powerful, lightweight, and easy-to-integrate RTSP server library for .NET MAUI on Android. Stream live camera feeds with MJPEG and H.264 codecs, featuring both RTSP and HTTP streaming capabilities.

## 🚀 Project Motivation

This project was born from a real need: I wanted to run an RTSP server on a custom Android device. However, since I'm not a fan of Java/Kotlin and currently focusing on C# for my specialization, I chose to develop this using .NET MAUI, a C# cross-platform framework that I love.

I quickly discovered a lack of existing libraries for RTSP streaming on Android in the C# ecosystem—especially for mobile devices. So I decided to mix both worlds: use Kotlin for low-level Android camera access, and C# for everything else.

## 🎯 Purpose and Vision

The aim is to offer a simple, easily integrable, and lightweight RTSP server for Android using MAUI. It supports raw camera frame capture and streaming over RTSP using MJPEG and H.264 codecs. I hope this helps other developers avoid the struggles I faced, and have a better, cleaner entry point into mobile RTSP streaming using MAUI and C#.

**MIT licensed. Free for everyone. No strings attached.**

## 📦 What's Inside?

### 🔹 Kotlin AAR Module
- Handles low-level camera access using Android's native APIs
- Designed to allow streaming from front, back, or both cameras
- Delivers frames in YUV_420 format via two callbacks
- Can work without a display (headless mode)

### 🔹 MAUI Integration
- Uses a .NET MAUI Library to integrate with the AAR
- Provides two camera services: `FrontCameraService` and `BackCameraService`
- Real-time frame capture at resolutions from 640x480 to 1920x1080+ (device dependent)
- Default frame rate: 45 FPS (adjusts dynamically)

### 🔹 RTSP Server (Pure C#)
- **Full RTSP Protocol Compliance**: Follows RTSP, RTP, and RTCP specifications
- **Dual Codec Support**:
  - **MJPEG**: Works smoothly, high bandwidth, no compression
  - **H.264**: Hardware-accelerated encoding, optimized for MediaTek devices
- **High Concurrency**: Can handle at least 12 simultaneous clients (tested)
- **Authentication**: Digest authentication included for basic security
- **Transport Modes**: UDP and TCP interleaved support
- **Dynamic Bitrate**: Automatic adjustment based on network conditions
- **Multiple Profiles**: Support for `/live/front` and `/live/back` routes

### 🔹 MJPEG HTTP Server
- Simple, independent MJPEG server for easy HTML display
- Allows usage of `<img src="http://your-device:port/mjpeg" />` in web pages
- Built to avoid duplicate frame processing—shares frames with the RTSP stream
- Dual camera support with separate endpoints

### 🔹 Utility Features
- Start/stop MJPEG or camera services via EventBus
- Snapshot capability using callbacks
- Built-in foreground service for background compatibility
- Simple demo project included
- Callbacks available to monitor connected clients, stream status, etc.
- Basic watchdog system to disconnect inactive clients
- Automatic resource cleanup and memory management

## 🛠️ Installation

### Prerequisites
- .NET 8.0 or later
- Android SDK API Level 26+ (Android 8.0+)
- Visual Studio 2022 with MAUI workload
- Android device or emulator

### NuGet Package
```xml
<PackageReference Include="BaluMediaServer.CameraStreamer" Version="1.1.9" />
```

### Manual Installation
1. Clone this repository
2. Add the project reference to your MAUI application
3. Add required permissions to your `AndroidManifest.xml`

## 📋 Required Permissions

Add these permissions to your `Platforms/Android/AndroidManifest.xml`:

```xml
<uses-permission android:name="android.permission.CAMERA" />
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_DATA_SYNC" />
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
```

## 🚀 Quick Start

### Basic RTSP Server Setup

```csharp
using BaluMediaServer.Services;
using BaluMediaServer.Models;

public class MainPage : ContentPage
{
    private Server _rtspServer;

    public MainPage()
    {
        InitializeComponent();
        
        // Request camera permissions
        RequestPermissions();
        
        // Initialize RTSP server with required authentication
        _rtspServer = new Server(
            Port: 7778,                    // RTSP port
            MaxClients: 12,                // Maximum concurrent clients
            Address: "0.0.0.0",           // Bind address
            Users: new Dictionary<string, string> 
            { 
                { "admin", "password123" } // Authentication is required
            }
        );
        
        // Start the server
        bool started = _rtspServer.Start();
        
        if (started)
        {
            Console.WriteLine("RTSP Server started successfully!");
            Console.WriteLine($"Front camera: rtsp://your-ip:7778/live/front");
            Console.WriteLine($"Back camera: rtsp://your-ip:7778/live/back");
        }
    }

    private async void RequestPermissions()
    {
        await Permissions.RequestAsync<Permissions.Camera>();
    }

    protected override void OnDisappearing()
    {
        _rtspServer?.Stop();
        base.OnDisappearing();
    }
}
```

### MJPEG HTTP Server Setup

```csharp
using BaluMediaServer.Services;
using BaluMediaServer.Repositories;

public class StreamingPage : ContentPage
{
    private MjpegServer _mjpegServer;

    public StreamingPage()
    {
        InitializeComponent();
        
        // Initialize MJPEG server
        _mjpegServer = new MjpegServer(port: 8089);
        
        // Start MJPEG streaming
        _mjpegServer.Start();
        
        // Or use EventBus for decoupled control
        EventBuss.SendCommand(BussCommand.START_MJPEG_SERVER);
    }

    protected override void OnDisappearing()
    {
        _mjpegServer?.Stop();
        EventBuss.SendCommand(BussCommand.STOP_MJPEG_SERVER);
        base.OnDisappearing();
    }
}
```

### Using with .NET MAUI

```csharp
using BaluMediaServer.Services;
using BaluMediaServer.Repositories;
using BaluMediaServer.Models;

public partial class MainPage : ContentPage
{
    private Server _rtspServer;
    private MjpegServer _mjpegServer;
    private bool _isStreaming = false;
    private int _clientCount = 0;

    public MainPage()
    {
        InitializeComponent();
        InitializeServers();
    }

    private async void InitializeServers()
    {
        // Request camera permissions
        await Permissions.RequestAsync<Permissions.Camera>();
        
        // Setup authentication (required)
        var users = new Dictionary<string, string>
        {
            { "admin", "password123" },
            { "viewer", "readonly" }
        };
        
        // Initialize servers
        _rtspServer = new Server(
            Port: 7778,
            MaxClients: 12,
            Address: "0.0.0.0",
            Users: users  // Authentication is required
        );
        
        _mjpegServer = new MjpegServer(port: 8089);
        
        // Subscribe to events
        Server.OnStreaming += OnStreamingStateChanged;
        Server.OnClientsChange += OnClientsChanged;
        
        // Start RTSP server
        bool started = _rtspServer.Start();
        
        if (started)
        {
            var localIP = GetLocalIPAddress();
            DisplayAlert("Server Started", 
                $"RTSP Server running at:\n" +
                $"rtsp://{localIP}:7778/live/back\n" +
                $"rtsp://{localIP}:7778/live/front", "OK");
        }
    }

    private void OnStreamingStateChanged(object? sender, bool isStreaming)
    {
        _isStreaming = isStreaming;
        MainThread.BeginInvokeOnMainThread(() => {
            // Update UI
            StatusLabel.Text = isStreaming ? "🔴 Live" : "⚫ Offline";
        });
    }

    private void OnClientsChanged(List<Client> clients)
    {
        _clientCount = clients.Count(c => c.Socket?.Connected ?? false);
        MainThread.BeginInvokeOnMainThread(() => {
            ClientCountLabel.Text = $"{_clientCount} client(s) connected";
        });
    }

    private void OnStartStreamingClicked(object sender, EventArgs e)
    {
        _mjpegServer.Start();
        EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
        EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
    }

    private void OnStopStreamingClicked(object sender, EventArgs e)
    {
        _mjpegServer.Stop();
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_BACK);
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_FRONT);
    }

    protected override void OnDisappearing()
    {
        _rtspServer?.Stop();
        _mjpegServer?.Stop();
        base.OnDisappearing();
    }
}
```

## 📖 Detailed API Documentation

### Server Class

The main RTSP server implementation that handles client connections and streaming.

#### Constructor
```csharp
public Server(
    int Port = 7778,                           // RTSP server port
    int MaxClients = 100,                      // Maximum concurrent clients
    string Address = "0.0.0.0",               // Bind address
    Dictionary<string, string> Users,           // Authentication users (required)
    bool BackCameraEnabled = true,              // Enable or disable back camera
    bool FrontCameraEnabled = true,             // Enable or disable front camera
    bool AuthRequired = true,                   // Disable full auth ignoring if a Users dict was passed (recommended just for testing)
    int MjpegServerQuality = 80                 // Sets a default Mjpeg Image compression quality by default, ideal if the image to display is small, avoiding using too much CPU
)

public Server(
    ServerConfiguration config // Simple class to configure the server
)
```

#### Methods
```csharp
// Start the RTSP server
public bool Start()

// Stop the server and cleanup resources
public void Stop()

// Add new user to the "database" *NO REBOOT REQUIRED
public bool AddUser(string username, string password)

// Remove user from the "database" *NO REBOOT REQUIRED
public bool RemoveUser(string username)

// Update user from the "database" *NO REBOOT REQUIRED
public bool UpdateUser(string username, string password)

// Static method to encode YUV data to JPEG
public static byte[] EncodeToJpeg(byte[] rawImageData, int width, int height, Android.Graphics.ImageFormatType format)
```

#### Events
```csharp
// Fired when streaming state changes
public static event EventHandler<bool>? OnStreaming;

// Fired when client list changes
public static event Action<List<Client>>? OnClientsChange;

// Fired when new frame is available from back camera (for general purpose use: snapshots, processing, etc.)
public static event EventHandler<FrameEventArgs>? OnNewBackFrame;

// Fired when new frame is available from front camera (for general purpose use: snapshots, processing, etc.)
public static event EventHandler<FrameEventArgs>? OnNewFrontFrame;
```

### MjpegServer Class

HTTP server for MJPEG streaming, perfect for web browser integration.

#### Constructor
```csharp
public MjpegServer(int port = 8089, int quality = 80)
```

#### Methods
```csharp
// Start the MJPEG HTTP server
public void Start()

// Stop the server
public void Stop()
```

#### Endpoints
- `http://your-ip:port/Back/` - Back camera stream
- `http://your-ip:port/Front/` - Front camera stream

### Camera Services

Low-level camera access services for frame capture.

#### BackCameraService / FrontCameraService
```csharp
// Start camera capture
public void StartCapture(int width = 640, int height = 480)

// Stop camera capture
public void StopCapture()

// Event fired when new frame is available
public event EventHandler<FrameEventArgs>? FrameReceived;

// Event fired when error occurs
public event EventHandler<string>? ErrorOccurred;
```

### EventBus System

Decoupled communication system for controlling services.

```csharp
// Available commands
public enum BussCommand
{
    START_CAMERA_FRONT,
    STOP_CAMERA_FRONT,
    START_CAMERA_BACK,
    STOP_CAMERA_BACK,
    START_MJPEG_SERVER,
    STOP_MJPEG_SERVER,
    SWITCH_CAMERA       // New command implemented, but without specific functions at the moment
}

// Send command
EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);

// Subscribe to commands
EventBuss.Command += (command) => {
    // Handle command
};
```

## 🔧 Advanced Configuration

### Authentication (Required)

Authentication is mandatory for the RTSP server. You must provide user credentials when initializing the server.

```csharp
// Required: Provide authentication users
var users = new Dictionary<string, string>
{
    { "admin", "secure_password" },
    { "viewer", "readonly_pass" },
    { "mobile", "mobile123" }
};

var server = new Server(
    Port: 7778,
    Users: users  // This parameter is required
);
```

**Note**: The server uses Digest authentication by default for security. Basic authentication is also supported for compatibility.

### H.264 Encoder Configuration

The H.264 encoder automatically optimizes for MediaTek devices but can be configured:

```csharp
// The encoder is automatically configured when streaming starts
// Default settings:
// - Bitrate: 2,000,000 bps (2 Mbps)
// - Frame rate: 25 FPS
// - Profile: Baseline
// - Keyframe interval: 2 seconds

// Dynamic bitrate adjustment happens automatically based on network conditions
```

### Multiple Camera Resolutions

```csharp
// Start cameras with custom resolution
var frontCamera = new FrontCameraService();
frontCamera.StartCapture(1920, 1080); // Full HD

var backCamera = new BackCameraService();
backCamera.StartCapture(1280, 720);   // HD
```

### Frame Capture for Snapshots and Processing

The server provides general-purpose frame events that can be used for snapshots, image processing, or custom applications. These events work independently of the streaming functionality.

```csharp
// Subscribe to frame events for custom processing
Server.OnNewBackFrame += (sender, frameArgs) => {
    // frameArgs.Data contains raw YUV data
    // frameArgs.Width, frameArgs.Height contain dimensions
    // frameArgs.Timestamp contains capture timestamp
    
    // Convert to JPEG for snapshot
    var jpegData = Server.EncodeToJpeg(
        frameArgs.Data, 
        frameArgs.Width, 
        frameArgs.Height, 
        Android.Graphics.ImageFormatType.Nv21
    );
    
    // Save snapshot
    await SaveSnapshotAsync(jpegData);
    
    // Or perform custom image processing
    ProcessFrame(frameArgs.Data, frameArgs.Width, frameArgs.Height);
};

Server.OnNewFrontFrame += (sender, frameArgs) => {
    // Same processing for front camera frames
    HandleFrontCameraFrame(frameArgs);
};

// Manually trigger camera capture for snapshots using EventBus
private async Task TakeSnapshot()
{
    // Start camera temporarily if not already running
    EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
    
    // Wait for frame capture
    await Task.Delay(500);
    
    // Stop camera if not needed for streaming
    if (!IsCurrentlyStreaming())
    {
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_BACK);
    }
}

// Example: Automatic snapshot every 30 seconds
private async void StartPeriodicSnapshots()
{
    while (true)
    {
        await TakeSnapshot();
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}
```

## 🌐 Network Usage

### RTSP URLs
- **Back Camera (H.264)**: `rtsp://your-ip:7778/live/back`
- **Front Camera (H.264)**: `rtsp://your-ip:7778/live/front`
- **Back Camera (MJPEG)**: `rtsp://your-ip:7778/live/back/mjpeg`
- **Front Camera (MJPEG)**: `rtsp://your-ip:7778/live/front/mjpeg`

### HTTP MJPEG URLs
- **Back Camera**: `http://your-ip:8089/Back/`
- **Front Camera**: `http://your-ip:8090/Front/`

### Connecting with Popular Clients

#### VLC Media Player
1. Open VLC
2. Go to Media → Open Network Stream
3. Enter: `rtsp://admin:password123@your-ip:7778/live/back`
4. Click Play

#### FFmpeg
```bash
# View stream
ffmpeg -i rtsp://admin:password123@your-ip:7778/live/back -f sdl output

# Record stream
ffmpeg -i rtsp://admin:password123@your-ip:7778/live/back -c copy output.mp4

# Re-stream to another server
ffmpeg -i rtsp://admin:password123@your-ip:7778/live/back -c copy -f rtsp rtsp://other-server/stream
```

#### OBS Studio
1. Add Source → Media Source
2. Uncheck "Local File"
3. Input: `rtsp://admin:password123@your-ip:7778/live/back`
4. Click OK

#### Web Browser (MJPEG only)
```html
<img src="http://your-ip:8089/Back/" alt="Live Stream" />
```

## 🔍 Troubleshooting

### Common Issues

#### Camera Permission Denied
```csharp
// Always request permissions before starting
var status = await Permissions.RequestAsync<Permissions.Camera>();
if (status != PermissionStatus.Granted)
{
    // Handle permission denied
    await DisplayAlert("Error", "Camera permission is required", "OK");
    return;
}
```

#### Port Already in Use
```csharp
try 
{
    var server = new Server(Port: 7778);
    bool started = server.Start();
    if (!started)
    {
        // Try alternative port
        server = new Server(Port: 7779);
        started = server.Start();
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Server start failed: {ex.Message}");
}
```

#### Network Connectivity Issues
```csharp
// Check network connectivity
var networkAccess = Connectivity.Current.NetworkAccess;
if (networkAccess != NetworkAccess.Internet)
{
    await DisplayAlert("Error", "No network connection", "OK");
    return;
}

// Get local IP address for clients to connect
var localIP = server.GetLocalIpAddress();
Console.WriteLine($"Connect to: rtsp://{localIP}:7778/live/back");
```

#### H.264 Encoding Issues
```csharp
// Check if device supports hardware encoding
try 
{
    var encoder = new MediaTekH264Encoder(640, 480);
    bool started = encoder.Start();
    if (!started)
    {
        // Fallback to MJPEG
        Console.WriteLine("H.264 not supported, using MJPEG");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"H.264 encoder error: {ex.Message}");
}
```

#### Performance Optimization

#### Memory Management
```csharp
// Properly dispose of resources
protected override void OnDisappearing()
{
    _rtspServer?.Stop();           // Stops all services
    _mjpegServer?.Stop();          // Stops HTTP server
    _frontCamera?.StopCapture();   // Stops camera capture
    _backCamera?.StopCapture();    // Stops camera capture
    
    base.OnDisappearing();
}
```

#### Reduce Latency & Improve Reliability
```csharp
// TCP transport is recommended over UDP for reliability
// The client (VLC, FFmpeg, etc.) will automatically negotiate transport
// To force TCP in VLC: go to Tools > Preferences > Input/Codecs > Network > RTP over RTSP (TCP)

// For FFmpeg, use TCP explicitly:
// ffmpeg -rtsp_transport tcp -i rtsp://admin:password@your-ip:7778/live/back output.mp4

// Reduce frame rate for better performance
frontCamera.StartCapture(640, 480); // Lower resolution = better performance

// Monitor client count and adjust quality
Server.OnClientsChange += (clients) => {
    if (clients.Count > 5)
    {
        // Reduce quality for multiple clients
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_FRONT);
    }
};
```

#### Transport Protocol Recommendations
```csharp
// Current known issue: UDP transport may fail when switching between transport modes
// Workaround: Use TCP transport which is more reliable

// In VLC Media Player:
// 1. Go to Tools > Preferences
// 2. Show settings: All
// 3. Navigate to Input / Codecs > Network
// 4. Set "RTP over RTSP (TCP)" to "Always"

// In FFmpeg:
// Use the -rtsp_transport tcp flag
```
## Images

### Streaming back camera (ffplay)
![Mobile App](Docs/RTSP_STREAM.png)
### Streaming front camera (ffplay)
![Mobile App](Docs/FRONT_RTSP_STREAM.png)
### Mobile App Interface
![Mobile App](Docs/DEMO.jpeg)


## ⚠️ Current Limitations

- **Platform Support**: Only Android 8.0+ (API 26+) is currently supported
- **Framework Support**: Currently tested only with .NET 9.0 (Partial support on .NET 8.0)
- **Configuration**: Limited runtime configuration options for codec parameters
- **Documentation**: No comprehensive API documentation yet (this README serves as primary docs)
- **iOS Support**: Not available (long-term roadmap item)
- **Image orientation**: Some devices can experiment image rotation
- **Image blazor preview**: Some devices can experiment some issues displaying the images fetched from the MJPEG Server, this can depend if the devices allow access to LoopBack or not (Not fixable at all)


## 🛣️ Roadmap

### Short Term (v1.1)
- ✅ Fix H.264 stream stutter issues
- ✅ Add support for multiple profiles/routes (`/live/front`, `/live/back`)
- ✅ Add user/password control panel
- ⬜ Fix image rotation
- ✅ Add bitrate/resolution configuration
- ✅ **Fix UDP transport reliability issues** (currently TCP is recommended FIXED)

### Medium Term (v1.2-1.3)
- ⬜ Add H.265 (HEVC) codec support
- ✅ Reduce streaming latency (target: <100ms)
- ⬜ Add comprehensive code documentation
- ⬜ NuGet package distribution
- ⬜ Add unit tests and integration tests

### Long Term (v2.0+)
- ⬜ iOS support via .NET MAUI
- ⬜ Audio streaming support
- ⬜ WebRTC integration
- ⬜ Cloud streaming integration
- ⬜ Advanced analytics and monitoring

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

**MIT License Summary:**
- ✅ Commercial use
- ✅ Modification
- ✅ Distribution  
- ✅ Private use
- ❌ Liability
- ❌ Warranty

## 💡 Why This Matters

There are few (if any) options to integrate RTSP servers with Android using C# and MAUI. This project bridges that gap. While it's still in early stages, it already provides a clean way to stream camera feeds using modern C# tooling—and it's open for everyone to contribute, extend, or just use freely.

## 🙏 Acknowledgments

- **MediaTek** for excellent hardware encoding support
- **.NET MAUI Team** for the amazing cross-platform framework
- **Android Camera2 API** for providing low-level camera access
- **RTSP/RTP Specifications** for the streaming protocols
- **Open Source Community** for inspiration and support

## 📬 Support & Contact

- **Issues**: Please use GitHub Issues for bug reports and feature requests
- **Discussions**: Use GitHub Discussions for general questions and ideas
- **Email**: danielulrichtamayo@gmail.com

### Getting Help

1. **Check the Documentation**: This README covers most use cases
2. **Search Issues**: Your question might already be answered
3. **Create an Issue**: Provide detailed information about your problem
4. **Community**: Join discussions with other developers

## Patch Notes

- v1.1.2: Adding at Server CTOR two new variables to handle if the front or back camera should be enabled, this avoid the problem that only one camera start on devices that can not handle both cameras at same time. 

- v1.1.3: Adding handling for auto-quality adjust based on rtcp control for MJPEG codec, allowing to increase or decrease the image quality to guarantee video stability over this codec.
-- Adding a preview (WIP) for video profiles allowing to create custom paths for this new profiles, will allow to set a custom resolution, bitrate and more.

- v1.1.4: Adding auth option into CTOR of Server class, to enable or disable auth on stream rtsp, adding feature to determina video quality into mjpeg server

- v1.1.5: Fixing EventBuss command on Server class, if the server was started do not raise the flag into it, and sometimes make the app crash due to "Port already in use" or even using excesive CPU on multiple MJPEG servers.
Adding to MJPEGServer preview of EventBuss to handle it by there, but needs sync with main server to avoid duplicate instances or commands.

- v1.1.6: Adding ArrayPool to avoid ovearhead at GC with multiple byte[] creations like in RTP Packets.
Adding .ConfigureAwait(false) on awaitable method to avoid context overhead, theorical from 100ms to 100 us, increase performance on fewer CPU resources devices.

- v1.1.7: Fixing MJPEG Codec bugs avoiding crashes, fixing Watchdog that close prematurly some connections, fixing some issues with the preview.

- v1.1.8: Fixing issues related with Camera Services, making that on camera or service closure do not allow to restart them.

- v1.1.9: Adding user/password handling options.

- v1.1.10: Adding a custom class 'ServerConfiguration' to handle more easily all the server configurations.

- v1.1.11: Fixing Server to allow Configuration Class, fixing MjpegServer disposal on Server class, fixing MjpegServer to set a fixed bitrate to 30 fps and fixing CPU leaks.

- v1.2.0: Adding new global encoder for compatiblity with multiple devices not only Mediatek and fixing some features from the server to handle clients.

- v1.3.1: Major H.264 Stability and Stutter Fix This release targets and resolves a series of core issues in the H.264 streaming logic that caused stutter, frame overlapping, and "two-frame" freezes. The stream is now significantly smoother and more stable.

  - Fixed Critical Timestamp Conversion:

  - Problem: The server was incorrectly converting the camera encoder's timestamps. We discovered the encoder provides timestamps in nanoseconds, but the server was treating them as microseconds. This resulted in RTP timestamps being 1000x too large, causing players to think a single frame should last for 30+ seconds, leading to a "two-frame" freeze.

  - Fix: The timestamp conversion logic in EncoderTimestampToRtp has been corrected to divide by 1,000,000,000.0 (nanoseconds) instead of 1,000,000.0 (microseconds).

  - Corrected RTP Marker Bit Logic:

  - Problem: The RTP "Marker Bit" (M-bit), which signals the end of a video frame, was being set incorrectly (e.g., on every small NAL unit). This confused decoders, causing them to render frames on top of each other or get stuck.

  - Fix: The server now correctly tracks all NAL units and fragments belonging to a single frame. The M-bit is now set only on the absolute last RTP packet of the last NAL unit for that frame, as required by the H.264 spec.

  - Removed Conflicting Stream Pacing:

  - Problem: The streaming loop had two "pacemakers" fighting each other:

  - A fixed Task.Delay trying to send at 45 FPS (22ms).

  - The H.264 encoder, which was producing frames at 25 FPS (40ms).

  - Fix: The fixed Task.Delay has been removed for H.264 streaming. The loop is now event-driven: it sends a frame as soon as the encoder provides one and loops immediately. If no new frame is ready, it waits a tiny 10ms (to prevent 100% CPU usage) and checks again. This lets the encoder, not the server loop, dictate the stream's framerate.

  - Eliminated Network Send Latency (Nagle's Algorithm):

  - Problem: For TCP streams, the OS was likely bundling small RTP packets together before sending them (Nagle's Algorithm). This is good for file transfers but terrible for real-time video, as it introduces small, random delays perceived as micro-stutter.

  - Fix: Nagle's Algorithm is now explicitly disabled (NoDelay = true) on all accepted client sockets, ensuring every RTP packet is sent to the network immediately.

  - Removed H.264 Frame Lock Contention:

  - Problem: The encoder thread (writing a new frame) and the network thread (reading that frame) were using the same lock. This meant one thread often had to wait for the other, causing a "hiccup" in frame delivery.

  - Fix: This lock has been completely replaced with a high-performance, lock-free Interlocked.Exchange operation. This allows the encoder and network threads to swap frame data atomically without ever blocking each other, resulting in a smoother handoff from camera to network.

---

**Thanks for checking out Balu Media Server!** 

Feel free to report bugs, suggest features, or fork and play around with the code.

**Let's make mobile RTSP with C# a thing!** 💪

---

*Made with ❤️ and C# • Open Source • MIT Licensed*