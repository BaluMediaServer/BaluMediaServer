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
- Real-time frame capture at resolutions from 320x240 up to **4K UHD (3840x2160)** (device dependent)
- Automatic encoder resolution validation with graceful fallback
- Dynamic memory-optimized buffer management for high resolutions
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
- Advanced watchdog system with 60-second inactivity timeout and comprehensive health monitoring
- Automatic resource cleanup and memory management

### 🔹 Modular RTSP Architecture (v1.5.8+)

The RTSP server has been refactored into focused, testable modules for better maintainability:

```
RTSP/
├── Server.cs                    # Main composition root (~700 lines)
├── Protocol/
│   ├── RtspProtocolHandler.cs   # RTSP request parsing and response handling
│   └── SdpGenerator.cs          # SDP generation for H.264/MJPEG
├── Transport/
│   ├── TransportManager.cs      # UDP/TCP sending, port management
│   ├── RtpPacketBuilder.cs      # RTP packet creation and NAL fragmentation
│   └── RtcpManager.cs           # RTCP sender reports and receiver feedback
├── Streaming/
│   ├── StreamingController.cs   # Main streaming orchestration
│   ├── H264EncoderManager.cs    # H.264 encoder lifecycle management
│   └── FramePacer.cs            # Frame delivery timing control
├── Security/
│   └── AuthenticationManager.cs # Digest/Basic authentication
└── ClientManagement/
    └── ClientManager.cs         # Client lifecycle and cleanup
```

**Benefits:**
- **Single Responsibility**: Each module handles one specific concern
- **Testability**: Interfaces enable dependency injection and unit testing
- **Maintainability**: Smaller files (~150-350 lines) are easier to navigate
- **Error Tracking**: Stack traces point to specific modules
- **Extensibility**: Modules can be extended or replaced independently

## 🛠️ Installation

### Prerequisites
- .NET 9.0 or later (partial support on .NET 8.0)
- Android SDK API Level 26+ (Android 8.0+)
- Visual Studio 2022 with MAUI workload
- Android device or emulator

### NuGet Package
```xml
<PackageReference Include="BaluMediaServer.CameraStreamer" Version="1.5.8" />
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
| **Models** | `Client`, `FrameEventArgs`, `H264FrameEventArgs`, `VideoProfile`, `VideoResolution`, `ServerConfiguration`, `RtspRequest`, `RtspAuth`, `EncoderInfo` |
| **Enums** | `AuthType`, `CodecType`, `BussCommand`, `TransportMode`, `VideoResolution` |
| **Services** | `Server`, `MjpegServer`, `FrontCameraService`, `BackCameraService` |
| **Encoders** | `H264Encoder`, `MediaTekH264Encoder` |
| **Utilities** | `EventBuss`, `FrameConverterHelper`, `FrameCallback` |
| **Interfaces** | `ICameraService`, `IAuthenticationManager`, `IClientManager`, `IH264EncoderManager`, `IRtcpManager`, `IRtpPacketBuilder`, `IRtspProtocolHandler`, `ISdpGenerator`, `IStreamingController`, `ITransportManager` |
| **RTSP Modules** | `AuthenticationManager`, `ClientManager`, `H264EncoderManager`, `RtcpManager`, `RtpPacketBuilder`, `RtspProtocolHandler`, `SdpGenerator`, `StreamingController`, `TransportManager`, `FramePacer` |

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
    string? CertificatePassword = null,        // Certificate password
    VideoResolution BackCameraResolution = VideoResolution.VGA_640x480,   // Back camera resolution
    VideoResolution FrontCameraResolution = VideoResolution.VGA_640x480   // Front camera resolution
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
    public bool EnableServer { get; set; } = true;                  // Enable/disable server startup
    public string BaseAddress { get; set; } = "0.0.0.0";            // Bind address
    public VideoProfile PrimaryProfile { get; set; } = new();       // Primary video profile
    public VideoProfile SecondaryProfile { get; set; } = new();     // Secondary video profile

    // Resolution configuration (affects H.264 encoder)
    public VideoResolution BackCameraResolution { get; set; } = VideoResolution.VGA_640x480;   // Back camera resolution preset
    public int BackCameraWidth { get; set; } = 0;                   // Custom back camera width (0 = use preset)
    public int BackCameraHeight { get; set; } = 0;                  // Custom back camera height (0 = use preset)
    public VideoResolution FrontCameraResolution { get; set; } = VideoResolution.VGA_640x480;  // Front camera resolution preset
    public int FrontCameraWidth { get; set; } = 0;                  // Custom front camera width (0 = use preset)
    public int FrontCameraHeight { get; set; } = 0;                 // Custom front camera height (0 = use preset)

    // Helper methods
    public int GetBackCameraWidth()  => BackCameraWidth > 0 ? BackCameraWidth : BackCameraResolution.GetWidth();
    public int GetBackCameraHeight() => BackCameraHeight > 0 ? BackCameraHeight : BackCameraResolution.GetHeight();
    public int GetFrontCameraWidth() => FrontCameraWidth > 0 ? FrontCameraWidth : FrontCameraResolution.GetWidth();
    public int GetFrontCameraHeight() => FrontCameraHeight > 0 ? FrontCameraHeight : FrontCameraResolution.GetHeight();

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
    public VideoResolution? Resolution { get; set; }        // Resolution preset (auto-sets Width, Height, and bitrates)
    public int Width { get; set; } = 640;                   // Video width (setting clears Resolution preset)
    public int Height { get; set; } = 480;                  // Video height (setting clears Resolution preset)
    public int MaxBitrate { get; set; } = 4000000;          // Maximum bitrate (bps)
    public int MinBitrate { get; set; } = 500000;           // Minimum bitrate (bps)
    public int Quality { get; set; } = 80;                  // JPEG quality (10-100)

    // Helper methods
    public int GetFrameBufferSize() => (Width * Height * 3) / 2;  // YUV420 buffer size for H.264
    public (int Width, int Height) GetDimensions() => (Width, Height);
}
```

### VideoResolution Enum

Predefined video resolution presets for camera capture and H.264 encoding. **Now supports up to 4K UHD!**

```csharp
public enum VideoResolution
{
    QVGA_320x240,       // 320x240 - Lowest quality, minimal bandwidth (~115 KB buffer)
    Low_480x360,        // 480x360 - Low quality (~259 KB buffer)
    VGA_640x480,        // 640x480 - Standard (default), most compatible (~460 KB buffer)
    SVGA_800x600,       // 800x600 - Enhanced standard (~720 KB buffer)
    HD_1280x720,        // 1280x720 - HD 720p (~1.38 MB buffer)
    FullHD_1920x1080,   // 1920x1080 - Full HD 1080p (~3.11 MB buffer)
    QHD_2560x1440,      // 2560x1440 - QHD/2K (~5.53 MB buffer)
    UHD_3840x2160       // 3840x2160 - 4K UHD (~12.4 MB buffer)
}

// Extension methods
public static int GetWidth(this VideoResolution resolution);
public static int GetHeight(this VideoResolution resolution);
public static int GetRecommendedMinBitrate(this VideoResolution resolution);
public static int GetRecommendedMaxBitrate(this VideoResolution resolution);
public static (int Width, int Height) GetDimensions(this VideoResolution resolution);
public static int GetFrameBufferSize(this VideoResolution resolution);
public static string GetDisplayName(this VideoResolution resolution);
```

**Resolution and H.264 Relationship:**
| Resolution | Frame Buffer | Min Bitrate | Max Bitrate |
|------------|-------------|-------------|-------------|
| QVGA (320x240) | ~115 KB | 300 Kbps | 500 Kbps |
| Low (480x360) | ~259 KB | 500 Kbps | 800 Kbps |
| VGA (640x480) | ~460 KB | 800 Kbps | 1.5 Mbps |
| SVGA (800x600) | ~720 KB | 1 Mbps | 2 Mbps |
| HD (1280x720) | ~1.38 MB | 2 Mbps | 4 Mbps |
| Full HD (1920x1080) | ~3.11 MB | 4 Mbps | 8 Mbps |
| QHD/2K (2560x1440) | ~5.53 MB | 8 Mbps | 16 Mbps |
| 4K UHD (3840x2160) | ~12.4 MB | 15 Mbps | 30 Mbps |

**Note:** High resolutions (QHD, 4K) require devices with hardware encoder support. The library automatically validates encoder capabilities and falls back to the nearest supported resolution if the requested resolution is not available.

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

// Set back camera resolution using preset (call before Start() for best results)
public void SetBackCameraResolution(VideoResolution resolution)

// Set back camera resolution using custom dimensions
public void SetBackCameraResolution(int width, int height)

// Set front camera resolution using preset (call before Start() for best results)
public void SetFrontCameraResolution(VideoResolution resolution)

// Set front camera resolution using custom dimensions
public void SetFrontCameraResolution(int width, int height)

// Get current back camera resolution
public (int Width, int Height) GetBackCameraResolution()

// Get current front camera resolution
public (int Width, int Height) GetFrontCameraResolution()

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

### Video Resolution Configuration

Resolution directly affects H.264 encoder performance and bandwidth requirements. Higher resolutions need more processing power and network bandwidth.

#### Using Resolution Presets (Recommended)

```csharp
using BaluMediaServer.Models;

// Option 1: Configure via ServerConfiguration
var config = new ServerConfiguration
{
    Port = 7778,
    BackCameraResolution = VideoResolution.HD_1280x720,    // HD for back camera
    FrontCameraResolution = VideoResolution.VGA_640x480,  // Standard for front camera
    Users = new Dictionary<string, string> { { "admin", "password" } }
};
var server = new Server(config);

// Option 2: Configure via constructor parameters
var server = new Server(
    Port: 7778,
    BackCameraResolution: VideoResolution.HD_1280x720,
    FrontCameraResolution: VideoResolution.VGA_640x480,
    Users: new Dictionary<string, string> { { "admin", "password" } }
);

// Option 3: Configure at runtime (before Start())
var server = new Server(Port: 7778, Users: users);
server.SetBackCameraResolution(VideoResolution.HD_1280x720);
server.SetFrontCameraResolution(VideoResolution.VGA_640x480);
server.Start();
```

#### Using Custom Resolutions

```csharp
// Via ServerConfiguration
var config = new ServerConfiguration
{
    BackCameraWidth = 800,
    BackCameraHeight = 600,
    FrontCameraWidth = 640,
    FrontCameraHeight = 480
};

// Via Server methods
server.SetBackCameraResolution(800, 600);
server.SetFrontCameraResolution(640, 480);

// Direct camera service (advanced usage)
var backCamera = new BackCameraService();
backCamera.StartCapture(1280, 720);
```

#### Resolution Selection Guidelines

| Use Case | Recommended Resolution | Notes |
|----------|----------------------|-------|
| Low bandwidth / Mobile data | QVGA (320x240) or Low (480x360) | Minimal data usage |
| Standard streaming | VGA (640x480) | Default, most compatible |
| High quality local network | HD (1280x720) | Good balance |
| Professional quality | Full HD (1920x1080) | Requires powerful device |
| Ultra-high quality | QHD/2K (2560x1440) | High-end devices only |
| Maximum quality | 4K UHD (3840x2160) | Flagship devices, high bandwidth required |

**Important:** The H.264 encoder buffer size is calculated as `(width * height * 3) / 2` for YUV420 format. Higher resolutions significantly increase memory usage. The library uses dynamic buffer management to prevent OOM crashes at high resolutions.

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


## 🧪 Testing

The project includes a comprehensive unit test suite using xUnit and FluentAssertions.

### Running Tests

```bash
# Navigate to test project
cd BaluMediaServer.Tests

# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity normal

# Run with code coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --filter "FullyQualifiedName~VideoProfileTests"
```

### Test Coverage

| Component | Tests | Coverage |
|-----------|-------|----------|
| VideoProfile | 22 | Quality clamping, name sanitization, defaults |
| ServerConfiguration | 27 | All property defaults, HTTPS config |
| EventBuss | 7 | Command propagation, subscriptions |
| RtspRequest | 21 | CSeq parsing, headers, properties |
| Enums | 57 | AuthType, CodecType, BussCommand validation |
| **Total** | **134** | **~90% of testable code** |

### Test Structure

```
BaluMediaServer.Tests/
├── Unit/
│   ├── Models/
│   │   ├── VideoProfileTests.cs
│   │   ├── ServerConfigurationTests.cs
│   │   └── RtspRequestTests.cs
│   ├── Infrastructure/
│   │   └── EventBussTests.cs
│   └── Enums/
│       └── EnumTests.cs
└── BaluMediaServer.Tests.csproj
```

### Note on Android-Dependent Code

Unit tests cover pure C# components. Android-dependent classes (camera services, encoders, network servers) require integration testing on an Android device/emulator.

## ⚠️ Current Limitations

- **Platform Support**: Only Android 8.0+ (API 26+) is currently supported
- **Framework Support**: Tested with .NET 9.0 (partial support on .NET 8.0)
- **iOS Support**: Not available (long-term roadmap item)
- **Image orientation**: Some devices may experience image rotation issues
- **Blazor preview**: Some devices may experience issues displaying images from the MJPEG Server, depending on LoopBack access restrictions


## 🛣️ Roadmap

### Completed (v1.1-v1.5.8)
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
- ✅ Unit testing infrastructure with xUnit
- ✅ **4K UHD and QHD resolution support**
- ✅ **High resolution OOM crash fix**
- ✅ **Automatic encoder resolution validation and fallback**
- ✅ **Dynamic memory-optimized buffer management**
- ✅ **Modular RTSP server architecture refactoring**

### Planned (v1.6+)
- ⬜ Fix image rotation on some devices
- ⬜ Add H.265 (HEVC) codec support
- ⬜ Add integration tests for Android-dependent code

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

- v1.5.3: Unit Testing Infrastructure. Added comprehensive unit test suite using xUnit and FluentAssertions.

  - **Test Project**: `BaluMediaServer.Tests` targeting `net9.0` with file linking approach for cross-targeting compatibility

  - **Test Coverage (134 tests)**:
    - `VideoProfileTests` (22 tests): Quality clamping, name sanitization, default values
    - `ServerConfigurationTests` (27 tests): All property defaults, HTTPS config, camera settings
    - `EventBussTests` (7 tests): Command propagation, multiple subscribers, unsubscribe handling
    - `RtspRequestTests` (21 tests): CSeq parsing, header handling, property initialization
    - `EnumTests` (57 tests): AuthType, CodecType, BussCommand value validation and parsing

  - **Tools**: xUnit, FluentAssertions, Coverlet for code coverage

  - **Run Tests**:
    ```bash
    cd BaluMediaServer.Tests
    dotnet test
    dotnet test --collect:"XPlat Code Coverage"
    ```

- v1.5.4: Critical Bug Fixes for Server Startup and MJPEG Streaming. This release addresses multiple issues that could cause the server to fail silently or MJPEG streaming to not transmit video.

  - **Fixed Wrong Event Unsubscription in Server.cs**:

  - Problem: When stopping the back camera via `BussCommand.STOP_CAMERA_BACK`, the code was incorrectly unsubscribing from `OnFrontFrameAvailable` instead of `OnBackFrameAvailable`. This caused event handler leaks and potential memory issues.

  - Fix: Corrected the event unsubscription to use `OnBackFrameAvailable` for the back camera service.

  - **Fixed MJPEG Server Initialization Issues**:

  - Problem: The `MjpegServer` was being created with default parameters at field initialization, causing:
    - Memory leak from orphaned event subscriptions (the initial instance subscribed to static events but was replaced in the constructor)
    - Lost configuration when MJPEG server was recreated (only `quality` parameter was passed, losing port, authentication, HTTPS settings, etc.)

  - Fix:
    - Added stored fields for all MJPEG server settings (`_mjpegServerPort`, `_mjpegUseHttps`, `_mjpegCertificatePath`, `_mjpegCertificatePassword`)
    - Created `CreateMjpegServer()` helper method that uses all stored configuration
    - Changed `_mjpegServer` to nullable with proper null checks throughout
    - All MJPEG server recreation points now use the helper method with full configuration

  - **Fixed Channel Initialization Race Condition in Camera Services**:

  - Problem: In both `BackCameraService` and `FrontCameraService`, the camera capture was started BEFORE the frame channel was created. This caused:
    - `NullReferenceException` if frames arrived immediately after starting capture
    - Potential frame loss during the race window
    - The `BoundedChannelFullMode.Wait` setting could block the native camera callback thread

  - Fix:
    - Reordered initialization to create the channel BEFORE starting camera capture
    - Changed `BoundedChannelFullMode.Wait` to `DropOldest` to prevent blocking the camera callback thread (real-time video should drop old frames, not block)
    - Added null checks in `OnFrameAvailable()`, `StopCapture()`, and `Dispose()` methods

  - **Impact**: These fixes resolve issues where the server would appear to start successfully but:
    - MJPEG streaming would not transmit any video
    - Camera services could crash on first frame
    - Event handlers could leak memory over time

- v1.5.5: Critical Server Startup Fix (EnableServer Flag). This release fixes a critical bug where servers created using `ServerConfiguration` would never start.

  - **Fixed Missing EnableServer Configuration Property**:

  - Problem: When using the `Server(ServerConfiguration config)` constructor, the internal `_enabled` flag was always set to `false`. This caused `Server.Start()` to return immediately without actually starting the server, as the start logic checks `if (_enabled && !IsRunning)` before proceeding.

  - Root Cause: The `ServerConfiguration` class was missing the `EnableServer` property. Since `bool` defaults to `false` in C#, any server created via the configuration constructor would have `_enabled = false`, silently preventing startup.

  - Fix:
    - Added `EnableServer` property to `ServerConfiguration` class with default value of `true`
    - Server constructor now correctly reads this value: `_enabled = configuration.EnableServer`
    - Added comprehensive XML documentation explaining the property's purpose

  - **Impact**: This was a critical bug that caused servers initialized with `ServerConfiguration` to appear to start (no errors thrown) but never actually accept connections. The `Start()` method would return `false` silently. This fix ensures servers start correctly by default.

  - **Migration Note**: Existing code using `ServerConfiguration` will now work correctly without any changes, as `EnableServer` defaults to `true`. If you need to disable a server, explicitly set `EnableServer = false`.

- v1.5.6: Camera Resource Management Fix. This release fixes a critical bug where cameras would continue running after all clients disconnected, wasting device resources.

  - **Fixed Camera Not Stopping When Clients Disconnect**:

  - Problem: When RTSP streaming started, the code set `_isCapturingBack = true` (or `_isCapturingFront`), but these flags were never reset when clients disconnected. The WatchDog checked `if (!_isCapturingBack)` before stopping cameras, so it would skip stopping them because the flag was still `true`.

  - Fix:
    - Removed the broken condition that prevented cameras from stopping
    - Now properly resets `_isCapturingBack` and `_isCapturingFront` to `false` when stopping cameras
    - Cameras now correctly stop when all RTSP clients disconnect

  - **Improved MJPEG Client Detection**:

  - Problem: The WatchDog only checked if MJPEG server was enabled (`_mjpegServerEnabled`), not if it actually had connected clients. This meant cameras would keep running even when MJPEG server had no clients.

  - Fix: Now checks `_mjpegServer?.ClientCount` to determine if MJPEG has active clients before deciding to keep cameras running.

  - **Added Catch-All Resource Cleanup**:

  - Fix: Added a second condition to catch the case where cameras are running (started by EventBuss or MJPEG) but all clients have disconnected:
    - If no RTSP clients are playing AND no MJPEG clients are connected AND cameras are running, cameras will now stop
    - Logs clearly indicate when cameras are stopped or kept running for clients

  - **Impact**: This fix prevents unnecessary battery drain and CPU usage when no clients are connected to the stream.

- v1.5.7: Configurable Video Resolution. This release adds full support for configuring camera resolution, allowing users to select from predefined presets or specify custom resolutions.

  - **New VideoResolution Enum**:

  - Added `VideoResolution` enum with common presets: QVGA (320x240), Low (480x360), VGA (640x480), SVGA (800x600), HD (1280x720), Full HD (1920x1080)
  - Each preset includes recommended bitrate settings for H.264 encoding
  - Extension methods provide helper functions: `GetWidth()`, `GetHeight()`, `GetRecommendedMinBitrate()`, `GetRecommendedMaxBitrate()`, `GetFrameBufferSize()`, `GetDisplayName()`

  - **ServerConfiguration Resolution Support**:

  - Added `BackCameraResolution` and `FrontCameraResolution` properties for preset selection
  - Added `BackCameraWidth`, `BackCameraHeight`, `FrontCameraWidth`, `FrontCameraHeight` for custom resolutions
  - Helper methods `GetBackCameraWidth()`, `GetBackCameraHeight()`, etc. resolve effective resolution

  - **Server Class Enhancements**:

  - Constructor now accepts `BackCameraResolution` and `FrontCameraResolution` parameters
  - New methods: `SetBackCameraResolution()`, `SetFrontCameraResolution()`, `GetBackCameraResolution()`, `GetFrontCameraResolution()`
  - Camera capture and H.264 encoder now use configured resolution

  - **VideoProfile Updates**:

  - Added `Resolution` property for preset-based configuration
  - Setting `Resolution` automatically updates `Width`, `Height`, `MinBitrate`, and `MaxBitrate`
  - Helper methods: `GetFrameBufferSize()`, `GetDimensions()`

  - **H.264 Encoder Considerations**:

  - Resolution directly affects encoder buffer size: `(width * height * 3) / 2` for YUV420
  - Higher resolutions require more processing power and bandwidth
  - Bitrate recommendations scale with resolution for optimal quality

  - **Impact**: Users can now easily configure video resolution to balance quality, bandwidth, and device performance. Default resolution remains VGA (640x480) for backward compatibility.

- v1.5.8: Modular Architecture Refactoring and High Resolution Support. This release refactors the monolithic Server.cs into focused, testable modules and adds support for resolutions up to 4K UHD.

  - **Major RTSP Server Modular Refactoring**:

  - Problem: The original `Server.cs` was 2,621 lines with 68 methods handling too many responsibilities (networking, protocol parsing, authentication, encoding, streaming, transport, etc.), making it difficult to maintain, test, and debug.

  - Fix: Refactored into 9 focused modules with clear interfaces:

    | Module | Responsibility | Lines |
    |--------|---------------|-------|
    | `RtspProtocolHandler` | RTSP request parsing and response generation | ~326 |
    | `SdpGenerator` | SDP generation for H.264 and MJPEG | ~173 |
    | `AuthenticationManager` | Digest/Basic authentication, nonce management | ~275 |
    | `TransportManager` | UDP/TCP transport, port allocation | ~254 |
    | `RtpPacketBuilder` | RTP packet creation, NAL fragmentation | ~292 |
    | `RtcpManager` | RTCP sender reports, receiver feedback | ~242 |
    | `H264EncoderManager` | H.264 encoder lifecycle, frame queues | ~435 |
    | `StreamingController` | Main streaming orchestration | ~340 |
    | `ClientManager` | Client lifecycle, cleanup, caching | ~153 |
    | `Server` (reduced) | Composition root, wiring | ~700 |

  - **New Directory Structure**:
    ```
    RTSP/
    ├── Server.cs
    ├── Protocol/
    │   ├── IRtspProtocolHandler.cs, RtspProtocolHandler.cs
    │   └── ISdpGenerator.cs, SdpGenerator.cs
    ├── Transport/
    │   ├── ITransportManager.cs, TransportManager.cs
    │   ├── IRtpPacketBuilder.cs, RtpPacketBuilder.cs
    │   └── IRtcpManager.cs, RtcpManager.cs
    ├── Streaming/
    │   ├── IH264EncoderManager.cs, H264EncoderManager.cs
    │   ├── IStreamingController.cs, StreamingController.cs
    │   └── FramePacer.cs
    ├── Security/
    │   └── IAuthenticationManager.cs, AuthenticationManager.cs
    └── ClientManagement/
        └── IClientManager.cs, ClientManager.cs
    ```

  - **Benefits**:
    - **Single Responsibility**: Each module handles one specific concern
    - **Testability**: Interfaces enable dependency injection and unit testing
    - **Maintainability**: Smaller files (~150-350 lines) are easier to navigate
    - **Error Tracking**: Stack traces now point to specific modules
    - **Extensibility**: Modules can be extended or replaced independently

  - **Added 4K UHD and QHD Resolution Support**:

  - New resolution presets: QHD/2K (2560x1440) and 4K UHD (3840x2160)
  - Encoder now supports fallback resolutions including 4K, QHD+, QHD, and FHD+
  - EncoderInfo now tracks `Supports4K`, `SupportsQHD`, `SupportsFullHD`, and `SupportsHD` capabilities

  - **Fixed High Resolution OOM Crashes**:

  - Problem: Streaming at resolutions higher than HD 720p (e.g., FullHD, 1920x1440, 4K) caused `OutOfMemoryError` crashes due to excessive frame buffer memory usage.

  - Root Causes Identified:
    - Fixed 25-frame camera buffer caused ~78MB memory usage at FullHD
    - No encoder resolution validation before MediaCodec configuration
    - No graceful fallback when encoder didn't support requested resolution

  - Fix:
    - **Dynamic Channel Capacity**: Camera services now calculate buffer capacity based on resolution. Target ~8MB max buffer with 2-10 frames depending on resolution (vs. fixed 25 frames before)
    - **Encoder Resolution Validation**: `IsResolutionSupported()` method checks if encoder supports the requested resolution before configuration
    - **Graceful Fallback**: `GetNearestSupportedResolution()` finds the nearest supported resolution if requested resolution isn't available
    - **ActualWidth/ActualHeight Properties**: Encoder exposes actual resolution being used after any fallback
    - **Server Dimension Updates**: Server tracks and updates stored dimensions when encoder falls back to different resolution

  - **EncoderInfo Enhancements**:

  - Added `MaxSupportedWidth` and `MaxSupportedHeight` properties
  - Added `Supports4K`, `SupportsQHD`, `SupportsFullHD`, `SupportsHD` boolean flags
  - These are populated during encoder evaluation for capability reporting

  - **Camera Library Improvements** (Kotlin AAR):

  - Dynamic ImageReader buffer sizing based on resolution
  - Memory pressure detection to prevent OOM in camera capture layer
  - Optimized buffer pool management for high-resolution frames

  - **Impact**: The codebase is now more maintainable and testable. Users can safely request high resolutions (including 4K) without crashes. The library will automatically fall back to the nearest supported resolution if the device's encoder doesn't support the requested resolution.

- v1.5.9: Connection Stability and Timeout Improvements. This release addresses premature disconnections by improving timeout handling, activity tracking, and error logging across all transport modes.

  - **Fixed Premature Client Disconnections**:

  - Problem: The 10-second inactivity timeout was too aggressive and would disconnect stable clients during network congestion or when TCP sends were timing out. Combined with a 5-second TCP send timeout, clients could be disconnected after just 2 consecutive send delays (10 seconds total).

  - Fix:
    - **Increased Inactivity Timeout**: From 10 seconds to 60 seconds
    - **Improved Activity Tracking**: `LastActivityTime` is now updated at the start of each streaming loop iteration, not just on successful sends. This prevents false inactivity timeouts as long as the streaming loop is actively running.
    - **Reduced TCP Send Timeout**: From 5 seconds to 3 seconds for faster stuck connection detection
    - With the new settings, clients can experience up to 20 consecutive TCP send timeouts (~60 seconds) before being disconnected, providing much better tolerance for temporary network issues.

  - **Enhanced RTCP Timeout Handling (UDP Mode)**:

  - Problem: The 60-second RTCP timeout was too aggressive for UDP clients that don't send RTCP packets regularly, causing premature disconnection of valid clients.

  - Fix:
    - **Increased RTCP Timeout**: From 60 seconds to 120 seconds
    - Added detailed logging for RTCP events (BYE packets, Receiver Reports, timeouts)
    - Better distinction between server shutdown and client timeout in error handling

  - **Improved Socket Health Detection**:

  - Problem: Socket.Poll() was using a 1ms timeout which could be too aggressive for slower but stable connections.

  - Fix:
    - **Increased Socket Poll Timeout**: From 1ms to 10ms
    - More forgiving detection of socket disconnection while still catching dead connections quickly

  - **Comprehensive Logging Enhancements**:

  - Added detailed logging throughout the connection lifecycle:
    - All disconnection events now log the specific reason (socket disconnected, inactivity timeout, consecutive errors)
    - Transport mode (TCP/UDP) included in disconnection logs
    - TCP/UDP send errors now log client IDs and consecutive error counts
    - Socket error codes logged for better debugging
    - Dead client detection logs explain why each client is marked as dead
    - WatchDog cleanup operations are now logged with counts

  - **Summary of New Timeout Values**:

    | Setting | Old Value | New Value | Purpose |
    |---------|-----------|-----------|---------|
    | Inactivity Timeout | 10s | 60s | Time before disconnecting idle clients |
    | TCP Send Timeout | 5s | 3s | Timeout for individual TCP send operations |
    | RTCP Timeout (UDP) | 60s | 120s | Timeout waiting for RTCP packets from UDP clients |
    | Socket Poll Timeout | 1ms | 10ms | Timeout for socket connectivity checks |

  - **Impact**: RTSP connections are now significantly more stable, especially over congested networks or with clients that have slower connections. The enhanced logging makes it much easier to diagnose any connection issues that do occur. Device connections remain stable even during temporary network hiccups or when send operations experience delays.

- v1.5.10: Major Performance Improvements - Event-Driven Architecture and Shared Encoding. This release addresses critical performance bottlenecks identified in performance analysis, delivering significantly improved frame rates, reduced CPU usage, and lower latency.

  - **Fixed Issue 2.2: Shared JPEG Encoding Infrastructure (HIGHEST IMPACT)**:

  - Problem: RTSP-MJPEG clients were encoding each frame synchronously within their streaming loops. This CPU-intensive operation (`YuvImage.CompressToJpeg`) blocked the entire streaming thread, causing severe frame drops and low FPS. Multiple clients would duplicate this expensive work for every frame.

  - Solution: Created `JpegEncoderService` - a centralized background encoding service:
    - **Single Encoding Per Frame**: Each frame is encoded only once, then shared across all MJPEG clients (both HTTP and RTSP)
    - **Background Tasks**: Encoding happens in dedicated background tasks using `System.Threading.Channels`
    - **Non-Blocking Delivery**: Clients consume pre-encoded JPEG frames without waiting for encoding
    - **Automatic Frame Dropping**: Bounded channels with `DropOldest` policy prevent memory buildup

  - Impact:
    - **Frame Rate**: RTSP-MJPEG improved from ~5-10 FPS to ~25-30 FPS
    - **CPU Usage**: Dramatically reduced, especially with multiple clients
    - **Scalability**: CPU usage no longer scales linearly with client count
    - **Client Isolation**: Slow clients don't block fast clients

  - **Fixed Issue 2.1: Event-Driven H.264 Frame Delivery**:

  - Problem: H.264 streaming relied on polling with `Task.Delay(10ms)` to check for available frames. This wasted CPU cycles and added artificial 10ms minimum latency to every frame.

  - Solution: Implemented async/await pattern with `Channel.Reader.ReadAsync()`:
    - Added `DequeueFrameAsync()` method to `IH264EncoderManager`
    - Replaced `ConcurrentQueue` with `System.Threading.Channels.Channel` in `H264EncoderManager`
    - Streaming threads now sleep efficiently until frames are available (zero CPU when idle)
    - Frames delivered immediately when available (no polling delay)

  - Impact:
    - **Latency**: Eliminated 10ms polling delay
    - **CPU Usage**: Threads sleep instead of spin-waiting
    - **Battery Life**: Reduced CPU usage improves battery on mobile devices
    - **Responsiveness**: Frames delivered immediately when encoder produces them

  - **Fixed Issue 2.4: Modernized H.264 Encoder Frame Queue**:

  - Problem: `H264Encoder` used `ConcurrentQueue` with manual frame dropping logic (while loop checking `Count`, manual dequeue).

  - Solution: Replaced with `System.Threading.Channels.Channel`:
    - Bounded channel with capacity of 2 frames
    - `DropOldest` policy automatically handles overflow
    - Simplified code by removing manual frame management

  - Impact:
    - **Code Quality**: Cleaner, more maintainable implementation
    - **Efficiency**: Better frame buffering with less overhead
    - **Reliability**: Automatic backpressure management

  - **Architecture Improvements**:

  - New Components:
    - `RTSP/Streaming/JpegEncoderService.cs`: Centralized JPEG encoding service
    - `EncodedJpegFrame` class: Represents pre-encoded JPEG frames

  - Updated Components:
    - `StreamingController`: Now consumes from shared JPEG service
    - `H264EncoderManager`: Event-driven async frame delivery
    - `H264Encoder`: Channel-based frame queuing
    - `Server`: Integrates JpegEncoderService and feeds frames to it

  - Design Patterns:
    - **Producer-Consumer**: Channels cleanly separate frame production from consumption
    - **Single Responsibility**: JpegEncoderService handles only encoding concerns
    - **Async/Await**: Modern .NET async patterns eliminate blocking and polling
    - **Bounded Buffers**: Automatic backpressure management prevents memory issues

  - **Performance Metrics**:

  | Metric | Before | After | Improvement |
  |--------|--------|-------|-------------|
  | RTSP-MJPEG FPS | 5-10 | 25-30 | **3-6x faster** |
  | H.264 Frame Latency | 10ms+ (polling) | Near-zero | **10ms+ saved per frame** |
  | CPU Usage (MJPEG) | High, scales per client | Low, shared encoding | **Linear to constant scaling** |
  | Multiple Clients | Each encodes separately | Shared single encode | **N-1 encodes eliminated** |

  - **Not Implemented (Deferred)**:

  - Issue 2.3 (ArrayPool for Frame Buffers): Deferred to future release
    - Requires invasive changes to camera service native interop
    - Would affect `FrameEventArgs` and all frame consumers
    - Risk/benefit analysis favors deferring given the substantial gains from other optimizations
    - Can be revisited if GC pressure becomes an issue at 1080p+ resolutions

  - **Testing Recommendations**:

  - RTSP-MJPEG: Connect 3+ clients simultaneously and verify smooth 25+ FPS on all
  - H.264: Verify low latency and no frame drops under load
  - Memory: Monitor GC collections during extended streaming sessions
  - CPU: Compare CPU usage vs v1.5.9
  - Mixed Load: Test concurrent H.264 + MJPEG clients

  - **Impact**: This release delivers the most significant performance improvements in the project's history. RTSP-MJPEG is now viable for production use with multiple concurrent clients. H.264 streaming has reduced latency and CPU overhead. The codebase uses modern .NET async patterns throughout for better efficiency and maintainability. See `PERFORMANCE_IMPROVEMENTS.md` for detailed technical analysis.

---

**Thanks for checking out Balu Media Server!** 

Feel free to report bugs, suggest features, or fork and play around with the code.

**Let's make mobile RTSP with C# a thing!** 💪

---

*Made with ❤️ and C# • Open Source • MIT Licensed*