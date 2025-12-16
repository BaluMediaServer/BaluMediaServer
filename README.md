# 📡 Balu Media Server - MAUI RTSP Server for Android

[![MIT License](https://img.shields.io/badge/License-MIT-green.svg)](https://choosealicense.com/licenses/mit/)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
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
- .NET 9.0 or later (partial support on .NET 8.0)
- Android SDK API Level 26+ (Android 8.0+)
- Visual Studio 2022 with MAUI workload
- Android device or emulator

### NuGet Package
```xml
<PackageReference Include="BaluMediaServer.CameraStreamer" Version="1.5.2" />
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

### Code Documentation

All classes in this library include comprehensive XML documentation comments for IntelliSense support. This provides:

- **Class-level summaries** describing the purpose of each component
- **Method documentation** with `<param>`, `<returns>`, and `<summary>` tags
- **Property documentation** explaining what each property represents
- **Event documentation** describing when events are raised

**Documented Classes:**
| Category | Classes |
|----------|---------|
| **Models** | `Client`, `FrameEventArgs`, `H264FrameEventArgs`, `VideoProfile`, `ServerConfiguration`, `RtspRequest`, `RtspAuth`, `EncoderInfo` |
| **Enums** | `AuthType`, `CodecType`, `BussCommand`, `TransportMode` |
| **Services** | `Server`, `MjpegServer`, `FrontCameraService`, `BackCameraService` |
| **Encoders** | `H264Encoder`, `MediaTekH264Encoder` |
| **Utilities** | `EventBuss`, `FrameConverterHelper`, `FrameCallback` |
| **Interfaces** | `ICameraService` |

### Server Class

The main RTSP server implementation that handles client connections and streaming.

#### Constructor
```csharp
public Server(
    int Port = 7778,                           // RTSP server port
    int MaxClients = 100,                      // Maximum concurrent clients
    string Address = "0.0.0.0",               // Bind address
    Dictionary<string, string>? Users = null,  // Authentication users (optional, default admin user is created)
    bool BackCameraEnabled = true,             // Enable or disable back camera
    bool FrontCameraEnabled = true,            // Enable or disable front camera
    bool AuthRequired = true,                  // Disable full auth ignoring if a Users dict was passed (recommended just for testing)
    int MjpegServerQuality = 80,               // Sets a default Mjpeg Image compression quality
    int MjpegServerPort = 8089,                // MJPEG HTTP server port
    bool UseHttps = false,                     // Enable HTTPS for MJPEG server
    string? CertificatePath = null,            // Path to SSL certificate
    string? CertificatePassword = null         // Certificate password
)

public Server(
    ServerConfiguration config // Simple class to configure the server
)
```

### ServerConfiguration Class

A configuration class that simplifies server initialization with all available options.

```csharp
public class ServerConfiguration
{
    public int Port { get; set; } = 7778;                           // RTSP server port
    public int MaxClients { get; set; } = 10;                       // Maximum concurrent clients
    public Dictionary<string, string> Users { get; set; } = new();  // Authentication users
    public int MjpegServerQuality { get; set; } = 80;               // MJPEG compression quality
    public int MjpegServerPort { get; set; } = 8089;                // MJPEG HTTP server port
    public bool AuthRequired { get; set; } = true;                  // Enable authentication
    public bool FrontCameraEnabled { get; set; } = true;            // Enable front camera
    public bool BackCameraEnabled { get; set; } = true;             // Enable back camera
    public bool StartMjpegServer { get; set; } = true;              // Auto-start MJPEG server
    public string BaseAddress { get; set; } = "0.0.0.0";            // Bind address
    public VideoProfile PrimaryProfile { get; set; } = new();       // Primary video profile
    public VideoProfile SecondaryProfile { get; set; } = new();     // Secondary video profile

    // HTTPS configuration for MJPEG server
    public bool UseHttps { get; set; } = false;                     // Enable HTTPS
    public string? CertificatePath { get; set; }                    // SSL certificate path
    public string? CertificatePassword { get; set; }                // Certificate password
}
```

### VideoProfile Class

Configuration for video encoding parameters.

```csharp
public class VideoProfile
{
    public string Name { get; set; } = "";                  // Profile name (used in URL path)
    public int Height { get; set; } = 640;                  // Video height
    public int Width { get; set; } = 480;                   // Video width
    public int MaxBitrate { get; set; } = 4000000;          // Maximum bitrate (bps)
    public int MinBitrate { get; set; } = 500000;           // Minimum bitrate (bps)
    public int Quality { get; set; } = 80;                  // JPEG quality (10-100)
}
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
public MjpegServer(
    int port = 8089,                              // HTTP server port
    int quality = 30,                             // JPEG compression quality (1-100)
    string bindAddress = "*",                     // Bind address ("*" for all interfaces)
    bool authEnabled = false,                     // Enable Basic HTTP authentication
    Dictionary<string, string>? users = null,    // Authentication users
    bool useHttps = false,                        // Enable HTTPS
    string? certificatePath = null,              // Path to SSL certificate
    string? certificatePassword = null           // Certificate password
)
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
- **Front Camera**: `http://your-ip:8089/Front/`

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
- **Framework Support**: Tested with .NET 9.0 (partial support on .NET 8.0)
- **iOS Support**: Not available (long-term roadmap item)
- **Image orientation**: Some devices may experience image rotation issues
- **Blazor preview**: Some devices may experience issues displaying images from the MJPEG Server, depending on LoopBack access restrictions


## 🛣️ Roadmap

### Completed (v1.1-v1.5)
- ✅ Fix H.264 stream stutter issues
- ✅ Add support for multiple profiles/routes (`/live/front`, `/live/back`)
- ✅ Add user/password control panel
- ✅ Add bitrate/resolution configuration
- ✅ Fix UDP transport reliability issues
- ✅ Reduce streaming latency (target: <100ms)
- ✅ Add comprehensive code documentation
- ✅ NuGet package distribution
- ✅ External network access for MJPEG server
- ✅ HTTPS support for MJPEG server
- ✅ Basic authentication for MJPEG server

### Planned (v1.6+)
- ⬜ Fix image rotation on some devices
- ⬜ Add H.265 (HEVC) codec support
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

- v1.4.0: Major H.264 Codec Overhaul and VLC Compatibility Fix. This release addresses critical issues in the H.264 implementation that caused stutter, timing problems, and complete playback failure on VLC and other strict players.

  - Fixed Critical Timestamp Unit Mismatch:

  - Problem: The encoder outputs timestamps in microseconds (PresentationTimeUs), but the server was treating them as nanoseconds. This resulted in RTP timestamps being 1000x smaller than expected, causing massive stutter, frame overlap, and timing desynchronization.

  - Fix: The timestamp conversion in `EncoderTimestampToRtp` now correctly converts microseconds to RTP units using fixed-point arithmetic: `(deltaUs * 9 + 50) / 100` (equivalent to `deltaUs * 90000 / 1_000_000`).

  - Added VLC Compatibility (sprop-parameter-sets in SDP):

  - Problem: VLC and many strict players require `sprop-parameter-sets` in the SDP to initialize the H.264 decoder. Without this, VLC would fail to decode the stream entirely.

  - Fix: The SDP now dynamically includes base64-encoded SPS and PPS in the `a=fmtp` line when available. The server caches these parameter sets as they're received from the encoder.

  - Fixed NAL Unit Extraction (Multiple NALs per Frame):

  - Problem: The encoder was treating each output buffer as a single NAL unit, even when it contained multiple NAL units (e.g., SEI + IDR, or SPS + PPS combined). This caused incomplete frames and decoder confusion.

  - Fix: Added `ExtractNalUnitsFromFrame` method that properly parses start codes and extracts all NAL units from encoder output. The server now sends each NAL unit as a separate RTP packet (or FU-A fragmented if large).

  - Fixed SPS/PPS Start Code Handling:

  - Problem: When extracting SPS/PPS from MediaFormat's csd-0/csd-1 buffers, the code assumed specific start code formats. Some encoders provide raw NAL data without start codes, others use 3-byte or 4-byte start codes.

  - Fix: Added robust `GetStartCodeLength`, `GetNalType`, and `EnsureStartCode` helper methods that handle all cases. Parameter sets are now normalized to 4-byte start codes for consistent handling.

  - Fixed Multi-Client Frame Delivery:

  - Problem: Using `Interlocked.Exchange` with null replacement caused frames to be consumed by one client, leaving other clients without frames.

  - Fix: Changed to non-destructive frame reading where each client tracks its own last-sent timestamp. Multiple clients can now receive the same frame, and per-client timestamp tracking prevents duplicate sends.

  - Improved Frame Dropping Strategy:

  - Problem: The encoder was aggressively dropping frames (keeping only 2 max), which could break B-frame prediction chains and cause visible stutter.

  - Fix: Frame queue limit increased to 3 with single-frame-at-a-time dropping. This provides better buffering while maintaining low latency.

  - Added RTCP Sender Reports:

  - Problem: VLC and other players use RTCP Sender Reports (SR) for clock synchronization and jitter buffer management. Without SR packets, players may exhibit poor sync and choppy playback.

  - Fix: The server now sends RTCP Sender Reports every 5 seconds. Each SR includes NTP timestamp, RTP timestamp, packet count, and octet count as per RFC 3550.

  - Enhanced Code Documentation:

  - Added XML documentation comments to all major methods explaining their purpose, parameters, and behavior.
  - Improved code readability with clear comments explaining RTP/RTCP protocol details.

- v1.4.1: Client Connection Management Overhaul and H.264 Reliability Fix. This release addresses critical issues with abrupt client disconnection handling and encoder buffer size mismatches that caused delayed reconnections and missing video.

  - Fixed Critical Client Collection Bug (ConcurrentBag → ConcurrentDictionary):

  - Problem: The server used `ConcurrentBag<Client>` with `TryTake()` for client cleanup. `TryTake()` removes a **random** element, not the specific client being cleaned up. This corrupted the client list over time, causing ghost clients and preventing proper cleanup.

  - Fix: Replaced `ConcurrentBag<Client>` with `ConcurrentDictionary<string, Client>`. Client cleanup now uses `TryRemove(client.Id, out _)` to remove the exact client being disconnected.

  - Added TCP Connection Timeout Detection:

  - Problem: `Socket.Connected` does not detect abrupt disconnections (network failure, process kill). The server would continue trying to stream to dead clients, blocking resources and preventing new clients from receiving video.

  - Fix: Added comprehensive connection health tracking:
    - `LastActivityTime` tracks last successful send per client
    - `ConsecutiveSendErrors` counts sequential failures
    - 5-second send timeout using `CancellationTokenSource` detects stuck connections
    - 30-second inactivity timeout in streaming loop catches zombie connections
    - Client marked as disconnected after 3 consecutive errors or socket exception

  - Fixed H.264 Encoder Buffer Size Mismatch:

  - Problem: The encoder was initialized with camera-reported dimensions (e.g., 640x480), but some cameras send frames with different actual sizes (e.g., 640x640). This caused `Input buffer too small` errors and dropped frames.

  - Fix: Added `CalculateDimensionsFromFrameSize()` method that:
    - Checks common resolutions against actual YUV420 frame size
    - Calculates dimensions using width/height hints when possible
    - Falls back to square aspect ratio calculation
    - Ensures encoder is always initialized with correct frame dimensions

  - Improved UDP Error Handling:

  - Problem: UDP send errors weren't properly tracked, allowing broken connections to persist.

  - Fix: UDP sends now track activity time and consecutive errors. Clients are disconnected after 5 consecutive UDP errors or when network is unreachable.

  - Enhanced Error Logging:

  - Added detailed logging for connection timeouts, send failures, and dimension mismatches to aid debugging.

- v1.5.0: Stability and Performance Improvements. This release focuses on connection reliability, faster disconnection detection, smoother video playback, and reduced latency.

  - Improved Socket Disconnection Detection:

  - Problem: `Socket.Connected` property doesn't reliably detect abrupt TCP disconnections (client crash, network loss, etc.). The server would continue streaming to dead connections for extended periods.

  - Fix: Added `IsSocketConnected()` method that uses `Socket.Poll()` to actively probe connection state. If poll returns readable but no data is available, the connection is confirmed closed. This detects dead connections within seconds instead of minutes.

  - Reduced Reconnection Time (WatchDog Optimization):

  - Problem: WatchDog ran every 60 seconds, causing long delays before the server detected all clients were gone and could reset state for new connections.

  - Fix: WatchDog interval reduced to 5 seconds. Also improved logic to check `IsPlaying` status alongside socket connection, actively clean up dead clients, and clear SPS/PPS caches when resetting streaming state.

  - Fixed Frame Queue for Smoother Playback:

  - Problem: H.264 frames were stored in single variables (`_latestH264FrameBack/Front`) using `Interlocked.Exchange`. If the encoder produced frames faster than they could be sent, frames would be overwritten and lost, causing stuttering.

  - Fix: Replaced single frame variables with `ConcurrentQueue<H264FrameEventArgs>` (max 5 frames buffer). Frames are now queued and sent in order, preventing loss during brief processing delays.

  - Optimized Encoder Input/Output Handling:

  - Problem: Encoder used 0ms timeout for buffer operations, causing missed buffers and aggressive frame dropping (max 1 frame in queue).

  - Fix:
    - Input buffer dequeue timeout increased to 10ms
    - Output buffer dequeue timeout increased to 10ms
    - Frame queue limit increased from 1 to 3 frames
    - Re-enabled sleep in encoding loop to prevent CPU spinning

  - Reduced Streaming Latency:

  - Fix: Multiple latency optimizations applied:
    - Socket buffers reduced from 256KB to 64KB (less buffering delay)
    - TCP_NODELAY explicitly set on all connections
    - I-frame interval reduced from 2s to 1s (faster stream recovery)
    - Inactivity timeout reduced from 30s to 10s (faster dead connection cleanup)

  - Enhanced Connection Health Checks:

  - Fix: Streaming loop now checks three conditions before each frame:
    - `IsSocketConnected()` for active connection state
    - Inactivity timeout (10 seconds with no successful send)
    - Consecutive send errors (disconnects after 3 failures)

  - Fixed Frame Stride Padding Handling:

  - Problem: Android cameras may include row stride padding in YUV frames, causing buffer size mismatches (e.g., 640x480 camera sending 614,398 bytes instead of expected 460,800).

  - Fix: Added frame size normalization via truncation to expected size. While this may cause minor artifacts on some devices, it prevents encoder crashes and ensures video delivery.

- v1.5.1: MJPEG Server External Access and Improvements. This release enables external device access to the MJPEG stream, making it usable from any device on the network via a simple `<img>` tag.

  - Enabled External Network Access:

  - Problem: MJPEG server was hardcoded to bind to `127.0.0.1` (localhost only), making it impossible for external devices to access the stream.

  - Fix: Changed default binding to `0.0.0.0` (all interfaces). Added configurable `bindAddress` parameter and wildcard prefix support for broader compatibility.

  - Added Optional Basic Authentication:

  - Problem: When exposed to the network, the MJPEG stream had no authentication, creating a security risk.

  - Fix: Added optional Basic HTTP authentication. When `AuthRequired` is enabled, clients must provide valid credentials. Authentication uses the same user database as the RTSP server.

  - Added CORS Headers for Web Integration:

  - Fix: MJPEG responses now include proper CORS headers (`Access-Control-Allow-Origin: *`) and cache control headers, enabling seamless integration with web pages on any domain.

  - Fixed Front Camera Client Handling:

  - Problem: `WriteDataAsync` only checked `_clientsBack` dictionary for client ID lookup, causing Front camera clients to not receive frames properly.

  - Fix: Added `isBackCamera` parameter to correctly handle both Front and Back camera clients with proper dictionary lookups.

  - Added Per-Client Timeout with Slow Client Protection:

  - Problem: One slow client could block frame delivery to all other clients due to `Task.WhenAll()` waiting for everyone.

  - Fix: Added `WriteDataAsyncWithTimeout()` wrapper with 2-second timeout per client. Slow clients are automatically disconnected without affecting others.

  - Fixed Memory Leak in Client Tracking:

  - Problem: `_clientLastFrameTime` dictionary was never cleaned up when clients disconnected, causing memory accumulation.

  - Fix: Client IDs are now properly removed from `_clientLastFrameTime` in all cleanup paths (normal disconnect, timeout, error).

  - Added Client Count Properties:

  - Fix: Added `ClientCount`, `BackClientCount`, and `FrontClientCount` properties for monitoring connected MJPEG clients.

  - Added MjpegServerPort Configuration:

  - Fix: MJPEG server port is now configurable via `ServerConfiguration.MjpegServerPort` (default: 8089) or constructor parameter.

  **Usage Example (External Access)**:
  ```html
  <!-- From any device on the network -->
  <img src="http://192.168.1.100:8089/Back/" alt="Live Stream" />

  <!-- With authentication -->
  <img src="http://admin:password123@192.168.1.100:8089/Back/" alt="Live Stream" />
  ```

- v1.5.2: Comprehensive Code Documentation. Added XML documentation comments to all public classes, methods, properties, and events.

  - **Models**: `FrameEventArgs`, `H264FrameEventArgs`, `Client`, `VideoProfile`, `ServerConfiguration`, `RtspRequest`, `RtspAuth`, `EncoderInfo`, `AuthType`, `CodecType`, `BussCommand`, `TransportMode`

  - **Services**: `Server`, `MjpegServer`, `FrontCameraService`, `BackCameraService`

  - **Encoders**: `H264Encoder` (general-purpose), `MediaTekH264Encoder` (MediaTek-optimized)

  - **Utilities**: `EventBuss`, `FrameConverterHelper`, `FrameCallback`, `ICameraService`

  - Benefits:
    - Full IntelliSense support in Visual Studio and VS Code
    - Auto-generated API documentation capability
    - Improved code maintainability and developer experience

---

**Thanks for checking out Balu Media Server!** 

Feel free to report bugs, suggest features, or fork and play around with the code.

**Let's make mobile RTSP with C# a thing!** 💪

---

*Made with ❤️ and C# • Open Source • MIT Licensed*