# 📡 Balu Media Server - MAUI RTSP Server for Android

[![MIT License](https://img.shields.io/badge/License-MIT-green.svg)](https://choosealicense.com/licenses/mit/)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
[![Android](https://img.shields.io/badge/Android-8.0%2B-green.svg)](https://developer.android.com/)
[![Platform](https://img.shields.io/badge/Platform-Android-brightgreen.svg)](https://developer.android.com/)

A powerful, lightweight, and easy-to-integrate RTSP server library for .NET MAUI on Android. Stream live camera feeds with MJPEG and H.264 codecs, featuring both RTSP and HTTP streaming capabilities, text overlay burn-in, and sub-100ms end-to-end latency.

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
- Ultra-low latency pipeline with minimal buffering (capacity=1 channels, batch RTP sends)
- Default frame rate: 45 FPS (adjusts dynamically)
- **Text overlay burn-in**: up to 4 configurable text slots stamped directly into encoded video frames (device name, IP, clock, custom text)

### 🔹 RTSP Server (Pure C#)
- **Full RTSP Protocol Compliance**: Follows RTSP, RTP, and RTCP specifications
- **Dual Codec Support**:
  - **MJPEG**: Works smoothly, high bandwidth, no compression
  - **H.264**: Hardware-accelerated encoding, optimized for MediaTek devices
- **Optional AAC Audio Track** (v1.5.28+): Hardware-encoded AAC-LC delivered as a second `m=audio` SDP track (`trackID=1`), packetized per RFC 3640 mpeg4-generic AAC-hbr. Toggle with `ServerConfiguration.EnableAudioTrack = true`; off by default so existing single-track clients see byte-identical SDP. Sample rate and channel count are configurable.
- **High Concurrency**: Can handle at least 12 simultaneous clients (tested)
- **Authentication**: Digest authentication included for basic security
- **Transport Modes**: UDP and TCP interleaved support
- **Smart Bitrate** (v1.5.29+): Auto-scaled to resolution (~0.2 bits/pixel/frame — enough headroom that fast motion doesn't smear into macroblocks), per-camera manual override honored verbatim, and optional RTCP adaptation clamped to `[ceiling/2, ceiling]` so a transient loss spike can't starve motion (`AdaptiveBitrate = false` pins it entirely)
- **30fps High-Resolution Pipeline** (v1.5.29+): Pooled frame buffers, hardware encoder pipelining, and µs-correct presentation timestamps deliver camera-rate streaming at 2K on MediaTek devices
- **Multiple Profiles**: Support for `/live/front` and `/live/back` routes
- **Robust Client Lifecycle**: Graduated error counting, timeout protection, and race-free cleanup
- **Stall Resilience — "STREAM STALLED" placeholder** (v1.5.30+): If the camera stops delivering frames (sensor-privacy toggle, device-owner policy, HAL hiccup), the server feeds a synthetic dark placeholder frame — black background, a "STREAM STALLED / Reconnecting camera…" message, and a live elapsed-seconds timer — into the H.264 and MJPEG streams instead of letting the client starve and disconnect. The RTSP/MJPEG session stays open, the viewer sees an informative dark window, and real video resumes seamlessly on the same session once the watchdog re-opens the camera. A separate path detects a *hung encoder* (camera still delivering) and restarts just the encoder (~1s) rather than the whole camera (10–30s on MediaTek). Toggle with `ServerConfiguration.StallPlaceholderEnabled` (default **on**)
- **Cross-SoC Compatibility**: Wall-clock RTP timestamps and MediaTek-safe encoder configuration
- **VLC Compatible**: Full RFC 2326/4566 compliance — works with VLC, ffplay, OBS, and any standards-compliant RTSP client
- **ONVIF Profile S** (v1.6.0+): Optional, opt-in ONVIF layer so the device drops into any VMS/NVR/ONVIF system. **WS-Discovery** auto-announces it on the LAN; the **Device** and **Media** SOAP services answer `GetDeviceInformation`, `GetCapabilities`, `GetProfiles`, `GetStreamUri` (returns the existing RTSP URL), `GetVideoEncoderConfigurations`, and `GetSnapshotUri` (a JPEG still). WS-UsernameToken auth reuses the existing user store. Enable via `ServerConfiguration.Onvif`; off by default so existing deployments are byte-for-byte unchanged. Adds only the discovery + description layer — the media pipeline is untouched
- **Text Overlay**: Configurable multi-slot text burned into video frames at the YUV level (zero decode overhead for clients)

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
- Advanced watchdog system with 60-second inactivity timeout, idle encoder shutdown, and comprehensive health monitoring
- Automatic resource cleanup and memory management
- **FrameOverlay**: text overlay stamped into YUV frames — up to 4 slots, 6 content types, 7 anchor positions, full RGB color, configurable size
- **BaluLogger**: subscribe to `BaluLogger.OnLog` to receive all internal library logs in-process — no ADB required; every `Log.Info/Warn/Error/Debug` call inside the library fires the event with level, tag, message, and timestamp

### 🔹 Modular RTSP Architecture (v1.5.8+)

The RTSP server has been refactored into focused, testable modules for better maintainability:

```
RTSP/
├── Server.cs                    # Main composition root (~700 lines)
├── H264Encoder.cs               # MediaCodec H.264 wrapper
├── AacEncoder.cs                # MediaCodec AAC-LC wrapper (audio/mp4a-latm)
├── Protocol/
│   ├── RtspProtocolHandler.cs   # RTSP request parsing and response handling (trackID-aware)
│   └── SdpGenerator.cs          # SDP generation for H.264 / MJPEG / optional AAC audio
├── Transport/
│   ├── TransportManager.cs      # UDP/TCP sending for video + audio tracks (sync + async)
│   ├── RtpPacketBuilder.cs      # RTP packet creation: H.264 NAL fragmentation + AAC-hbr
│   └── RtcpManager.cs           # RTCP sender reports and receiver feedback
├── Streaming/
│   ├── StreamingController.cs   # Main streaming orchestration (per-client video + audio loops)
│   ├── H264EncoderManager.cs    # H.264 encoder lifecycle management
│   ├── AacEncoderManager.cs     # AAC encoder lifecycle + per-client audio frame fan-out
│   ├── JpegEncoderService.cs    # Shared JPEG encoding for MJPEG clients
│   └── FramePacer.cs            # Frame delivery timing and burst throttling
├── Security/
│   └── AuthenticationManager.cs # Digest/Basic authentication
└── ClientManagement/
    └── ClientManager.cs         # Client lifecycle and cleanup

Services/
├── BackCameraService.cs         # Back camera capture (JNI-free processing thread)
├── FrontCameraService.cs        # Front camera capture (JNI-free processing thread)
├── AudioCaptureService.cs       # Microphone capture (Android AudioRecord, 1024-sample PCM frames)
├── MjpegServer.cs               # HTTP MJPEG streaming server
├── FrameOverlay.cs              # YUV-level text overlay burn-in (up to 4 slots)
├── StallPlaceholder.cs          # "STREAM STALLED" dark-window NV21 frame shown while the camera recovers
└── Onvif/                       # Optional ONVIF Profile S layer (opt-in)
    ├── OnvifModels.cs           # OnvifDeviceContext + OnvifProfile (Android-free data)
    ├── OnvifSoap.cs             # SOAP 1.2 namespaces, operation parsing, envelope/fault (Android-free)
    ├── OnvifSecurity.cs         # WS-UsernameToken PasswordDigest validation (Android-free)
    ├── OnvifDeviceService.cs    # Device service responses: info/capabilities/services/scopes (Android-free)
    ├── OnvifMediaService.cs     # Media service responses: profiles/stream URI/snapshot URI (Android-free)
    ├── WsDiscoveryMessages.cs   # WS-Discovery Hello/Bye/ProbeMatch builders + parser (Android-free)
    ├── OnvifServer.cs           # HttpListener SOAP transport shell (port 8090)
    └── WsDiscoveryService.cs    # UDP multicast transport + Android multicast lock

Interfaces/
├── ICameraService.cs            # Camera capture contract
└── IAudioCaptureService.cs      # Microphone capture contract

Models/
├── FrameEventArgs.cs            # Raw YUV video frame payload
├── H264FrameEventArgs.cs        # Encoded H.264 frame (NAL units + SPS/PPS)
├── AudioFrameEventArgs.cs       # Raw PCM-16 audio frame payload
└── AacFrameEventArgs.cs         # Encoded AAC access unit (+ AudioSpecificConfig on first frame)

BaluLogger.cs                    # Static logger: writes to logcat + fires OnLog event
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
<PackageReference Include="BaluMediaServer.CameraStreamer" Version="1.5.29" />
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
<!-- Only required when ServerConfiguration.EnableAudioTrack = true -->
<uses-permission android:name="android.permission.RECORD_AUDIO" />
<!-- Only required when ONVIF is enabled (ServerConfiguration.Onvif.Enabled = true) — lets the
     device receive WS-Discovery multicast Probes so VMS/NVR systems can auto-discover it -->
<uses-permission android:name="android.permission.CHANGE_WIFI_MULTICAST_STATE" />
```

> **Runtime permission for audio:** `RECORD_AUDIO` is a [dangerous permission](https://developer.android.com/guide/topics/permissions/overview#dangerous-permission-prompt). Request it at runtime (e.g. `await Permissions.RequestAsync<Permissions.Microphone>()`) before calling `server.Start()` when `EnableAudioTrack` is enabled. If the runtime grant is missing, the camera/video pipeline keeps working normally and the audio track stays silent — `AudioCaptureService` raises `ErrorOccurred` instead of crashing.

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
using BaluMediaServer.RTSP;

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
using BaluMediaServer.RTSP;
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
| **Enums** | `AuthType`, `CodecType`, `BussCommand`, `TransportMode`, `VideoResolution`, `OverlayContent`, `AnchorPoint` |
| **Services** | `Server`, `MjpegServer`, `FrontCameraService`, `BackCameraService`, `FrameOverlay` |
| **Logging** | `BaluLogger`, `BaluLogEventArgs`, `BaluLogLevel` |
| **Overlay Types** | `OverlaySlot`, `OverlayColor` |
| **Encoders** | `H264Encoder`, `MediaTekH264Encoder` |
| **Utilities** | `EventBuss`, `FrameConverterHelper`, `FrameCallback` |
| **Interfaces** | `ICameraService`, `IAuthenticationManager`, `IClientManager`, `IH264EncoderManager`, `IRtcpManager`, `IRtpPacketBuilder`, `IRtspProtocolHandler`, `ISdpGenerator`, `IStreamingController`, `ITransportManager` |
| **RTSP Modules** | `AuthenticationManager`, `ClientManager`, `H264EncoderManager`, `JpegEncoderService`, `RtcpManager`, `RtpPacketBuilder`, `RtspProtocolHandler`, `SdpGenerator`, `StreamingController`, `TransportManager`, `FramePacer` |

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

    // Text overlay (Android only, H.264 streams only)
    // null  → back camera uses the default layout (device name + clock, bottom-left)
    // []    → overlay disabled
    // [...] → custom slots (up to 4)
    public OverlaySlot[]? BackCameraOverlaySlots { get; set; }      // Back camera H.264 overlay
    // null  → no overlay on front camera (no default)
    // [...] → custom slots (up to 4)
    public OverlaySlot[]? FrontCameraOverlaySlots { get; set; }     // Front camera H.264 overlay

    // AAC audio track (v1.5.28+) — off by default so existing clients see byte-identical SDP.
    // When true, an m=audio AAC-LC track (trackID=1) is advertised in the SDP and the
    // microphone is captured + encoded only while at least one client is connected.
    // Requires android.permission.RECORD_AUDIO in the host manifest and runtime grant.
    public bool EnableAudioTrack { get; set; } = false;             // Enable AAC audio stream
    public int  AudioSampleRateHz { get; set; } = 44100;            // 44100 / 48000 / 22050 / 16000 / 8000
    public int  AudioChannels { get; set; } = 1;                    // 1 = mono, 2 = stereo

    // Stall resilience (v1.5.30+) — when true (default), a stalled camera shows a dark
    // "STREAM STALLED" placeholder (black frame + message + live elapsed timer) instead of
    // dropping the client; real video resumes on the same session once the camera recovers.
    public bool StallPlaceholderEnabled { get; set; } = true;       // Show dark placeholder on stall

    // ONVIF Profile S (v1.6.0+) — null (default) = disabled. When .Enabled is true, the device
    // is discoverable over WS-Discovery and exposes the Device/Media SOAP services on .Port.
    public OnvifOptions? Onvif { get; set; }                        // ONVIF Profile S options (off by default)
}

// OnvifOptions (v1.6.0+)
public class OnvifOptions
{
    public bool   Enabled { get; set; } = false;                    // Master switch
    public int    Port { get; set; } = 8090;                        // ONVIF SOAP/HTTP port
    public string Manufacturer { get; set; } = "Balu";              // GetDeviceInformation
    public string Model { get; set; } = "BaluMediaServer";          // GetDeviceInformation + name scope
    public string FirmwareVersion { get; set; } = "1.6.0";          // GetDeviceInformation
    public string SerialNumber { get; set; } = "";                  // Set a unique value per device
    public string HardwareId { get; set; } = "balu-1";              // GetDeviceInformation + hardware scope
}
```

### Audio Streaming (AAC over RTP)

The library can optionally publish a hardware-encoded **AAC-LC** audio stream alongside the H.264 video, exposed as a second `m=audio` track in the SDP (`a=control:trackID=1`). The feature is **off by default** so existing clients see byte-identical SDP and no microphone is opened.

**Enabling it** (via `ServerConfiguration`):

```csharp
var server = new Server(new ServerConfiguration
{
    Port = 7778,
    AuthRequired = false,
    BackCameraResolution = VideoResolution.HD_720p,

    // Add an AAC-LC m=audio track to the RTSP SDP
    EnableAudioTrack  = true,
    AudioSampleRateHz = 44100,   // 44100 / 48000 / 22050 / 16000 / 8000
    AudioChannels     = 1,       // 1 = mono, 2 = stereo
});
server.Start();
```

> Don't forget `android.permission.RECORD_AUDIO` in your manifest **and** a runtime permission grant — see [Required Permissions](#-required-permissions).

**What the client sees** (SDP excerpt):

```
m=video 0 RTP/AVP 96
a=rtpmap:96 H264/90000
a=control:trackID=0
m=audio 0 RTP/AVP 97
a=rtpmap:97 mpeg4-generic/44100/1
a=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;config=1208;sizelength=13;indexlength=3;indexdeltalength=3
a=control:trackID=1
```

**Verifying with ffplay:**

```bash
ffplay -loglevel verbose -rtsp_transport tcp rtsp://your-ip:7778/live/back
```

You should see both streams listed in `Input #0`:

```
Stream #0:0: Video: h264 (Baseline), ... 640x480
Stream #0:1: Audio: aac, 44100 Hz, mono, fltp
```

**How it works under the hood:**

| Stage          | Component                                                 |
|----------------|-----------------------------------------------------------|
| Capture        | `Services/AudioCaptureService.cs` — `AudioRecord` reads 1024-sample PCM-16 chunks (one AAC-LC access unit each) on a dedicated background thread. |
| Encode         | `RTSP/AacEncoder.cs` — `MediaCodec("audio/mp4a-latm")` with `AAC-LC` profile. Captures `csd-0` (AudioSpecificConfig) on first output. |
| Fan-out        | `RTSP/Streaming/AacEncoderManager.cs` — one shared encoder, per-client bounded channels (capacity 4, `DropOldest`). |
| Packetize      | `RTSP/Transport/RtpPacketBuilder.BuildAacRtpPacket` — RFC 3640 `mode=AAC-hbr`, one access unit per packet: `[12-byte RTP][AU-headers-length=0010][AU-header=(size<<3)][AAC AU bytes]`. |
| Send           | `RTSP/Transport/TransportManager.SendAudioBatchSync` — routes through `Client.AudioRtpChannel` (TCP interleaved) or `Client.AudioUdpSocket` / `Client.AudioRtpEndPoint` (UDP). Shares the per-client `SendLock` with video so TCP framing stays atomic. |
| SDP sync       | `RTSP/Protocol/SdpGenerator.UpdateAudioSpecificConfig` — the first encoder ASC overrides the hardcoded `config=` value, keeping the SDP truthful even if a vendor codec reports something exotic. |

**Lifecycle:**
- The microphone + AAC encoder start on the first `SETUP` that touches `trackID=1` (mirrors the `PreStartCameraAndEncoder` warm-up used for H.264).
- The watchdog stops both when `ClientCount == 0`, mirroring the H.264 idle-stall policy.
- `Server.Stop()` and `Dispose()` tear the audio pipeline down alongside the camera pipeline.

**Independent RTP streams per RFC 3550 §5.1:**
The audio track has its own `SSRC`, sequence number, and 44.1 kHz (or configured) RTP timestamp space, all stored on the `Client` instance in `AudioSsrcId`, `AudioSequenceNumber`, `AudioRtpTimestamp`, `AudioBaseEncoderTimestamp`, and `AudioBaseRtpTimestamp` — fully isolated from the video state.

**Known limitations (planned for v1.6+):**
- No RTCP Sender Reports yet for the audio track (video SR works as before).
- No explicit lip-sync alignment between the H.264 and AAC streams — both use a wall-clock-based timestamp anchor and align within a few frames, but rigorous A/V sync hardening is on the roadmap.
- One microphone per server — there's no per-camera audio source.

### VideoProfile Class

Configuration for video encoding parameters.

```csharp
public class VideoProfile
{
    public string Name { get; set; } = "";                  // Profile name (used in URL path)
    public VideoResolution? Resolution { get; set; }        // Resolution preset (auto-sets Width, Height, and bitrates)
    public int Width { get; set; } = 640;                   // Video width (setting clears Resolution preset)
    public int Height { get; set; } = 480;                  // Video height (setting clears Resolution preset)
    public int MaxBitrate { get; set; } = 20000000;         // Max bitrate / RTCP recovery ceiling (bps)
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

// --- Bitrate control (since v1.5.29) ---

// Set back camera H.264 bitrate (bits/sec). Honored exactly, even below the auto
// recommendation. Pass 0 (or <=0) for automatic resolution-scaled bitrate.
public void SetBackCameraBitrate(int bitrate)

// Set front camera H.264 bitrate (bits/sec). Same semantics as the back camera.
public void SetFrontCameraBitrate(int bitrate)

// Switch a camera back to automatic (resolution-scaled) bitrate
public void SetBackCameraAutoBitrate()
public void SetFrontCameraAutoBitrate()

// Get the effective bitrate currently in use (manual if set, otherwise auto)
public int GetBackCameraBitrate()
public int GetFrontCameraBitrate()

// Static: compute the recommended auto bitrate for a resolution (e.g. for a UI default)
public static int GetRecommendedBitrate(int width, int height)

// Enable/disable RTCP-driven adaptive bitrate. When disabled, the encoder is pinned to its
// configured auto/manual bitrate and RTCP reports are ignored (manual value honored verbatim).
public void SetAdaptiveBitrate(bool enabled)
public bool IsAdaptiveBitrateEnabled()

// Static method to encode YUV data to JPEG
public static byte[] EncodeToJpeg(byte[] rawImageData, int width, int height, Android.Graphics.ImageFormatType format)
```

### Text Overlay (FrameOverlay)

`FrameOverlay` stamps configurable text directly into the Y (luma) and UV (chroma) planes of NV21 frame buffers **before** MediaCodec encodes them. Clients receive text as part of the video — no extra decode work, no network overhead.

> **H.264 only.** The overlay is applied to frames fed to the hardware H.264 encoder. MJPEG streams are not affected.

- Up to **4 independent text slots** per overlay instance
- **Content types**: `DeviceName`, `IpAddress`, `DateTime`, `Date`, `Time`, `Custom`
- **Anchor positions**: `TopLeft`, `TopCenter`, `TopRight`, `BottomLeft`, `BottomCenter`, `BottomRight`, `Absolute`
- **Full RGB color** via `OverlayColor` (pre-built: White, Yellow, Orange, Red, Green, Cyan, Gray)
- **Dynamic refresh**: static slots rendered once; `Time`/`DateTime` refresh each second; `IpAddress` each minute
- **Per-frame hot path ≈ 2 µs** at 1280×720 (byte-level stamp, no allocation)
- **BT.601 limited-range** YUV conversion (Y ∈ [16,235], Cb/Cr ∈ [16,240])
- **Drop shadow** (1 px black offset) rendered automatically for readability on any background

#### Integration via ServerConfiguration

The overlay is configured through `ServerConfiguration` — the server lazily constructs the `FrameOverlay` instance when the first frame arrives (so the frame dimensions are known).

```csharp
var config = new ServerConfiguration
{
    // null  → default layout: device name + clock stacked in bottom-left
    // []    → overlay disabled
    // [..] → custom slots (see below)
    BackCameraOverlaySlots  = null,   // use default for back camera
    FrontCameraOverlaySlots = null,   // null = no overlay on front camera (no default)
};
```

#### Quick Start — Default Layout

The default layout places the device name and a live clock in the bottom-left corner, auto-spaced so they don't overlap:

```csharp
// Back camera: device name + clock in bottom-left (default when null)
BackCameraOverlaySlots = null,

// Back camera: explicitly use the default layout
BackCameraOverlaySlots = null,  // same thing — null always means "use default"

// Disable overlay entirely
BackCameraOverlaySlots = Array.Empty<OverlaySlot>(),
```

#### Custom Overlay

```csharp
BackCameraOverlaySlots = new[]
{
    // Slot 1 — live clock, bottom-left
    new OverlaySlot
    {
        Content  = OverlayContent.Time,
        Anchor   = AnchorPoint.BottomLeft,
        MarginX  = 10, MarginY = 10,
        TextSize = 28f,
        Color    = OverlayColor.White,
    },

    // Slot 2 — device name above clock
    new OverlaySlot
    {
        Content  = OverlayContent.DeviceName,
        Anchor   = AnchorPoint.BottomLeft,
        MarginX  = 10, MarginY = 48,   // 48 px above bottom edge
        TextSize = 32f,
        Color    = OverlayColor.Yellow,
    },

    // Slot 3 — IP address, top-right corner
    new OverlaySlot
    {
        Content  = OverlayContent.IpAddress,
        Anchor   = AnchorPoint.TopRight,
        MarginX  = 10, MarginY = 10,
        TextSize = 24f,
        Color    = OverlayColor.Cyan,
    },

    // Slot 4 — custom label at exact pixel position
    new OverlaySlot
    {
        Content    = OverlayContent.Custom,
        CustomText = "CAM-1",
        Anchor     = AnchorPoint.Absolute,
        MarginX    = 20, MarginY = 20,  // raw (x, y) of the text box top-left
        TextSize   = 20f,
        Color      = new OverlayColor(255, 165, 0),  // custom orange
    },
},
```

#### OverlaySlot Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Content` | `OverlayContent` | `Custom` | What text to display |
| `CustomText` | `string?` | `null` | Text shown when `Content == Custom` |
| `Anchor` | `AnchorPoint` | `BottomLeft` | Which corner/edge the slot is anchored to |
| `MarginX` | `int` | `10` | px from the anchor edge (horizontal); raw X when `Absolute` |
| `MarginY` | `int` | `10` | px from the anchor edge (vertical); raw Y when `Absolute` |
| `TextSize` | `float` | `32f` | Font size in points |
| `Color` | `OverlayColor` | `White` | Text color |

#### OverlayContent Enum

| Value | Description | Refresh |
|-------|-------------|---------|
| `DeviceName` | `Build.Manufacturer` + `Build.Model` | Static (once) |
| `IpAddress` | Local LAN IPv4 address | Every minute |
| `DateTime` | `yyyy-MM-dd HH:mm:ss` | Every second |
| `Date` | `yyyy-MM-dd` | Every day |
| `Time` | `HH:mm:ss` | Every second |
| `Custom` | Text from `OverlaySlot.CustomText` | Static (once) |

#### AnchorPoint Enum

| Value | Description |
|-------|-------------|
| `TopLeft` / `TopCenter` / `TopRight` | Anchored to top edge; `MarginY` = px from top |
| `BottomLeft` / `BottomCenter` / `BottomRight` | Anchored to bottom edge; `MarginY` = px from bottom |
| `Absolute` | `MarginX`/`MarginY` are raw pixel coordinates (top-left of text bounding box) |

#### OverlayColor

```csharp
// Named presets
OverlayColor.White   // (255, 255, 255)
OverlayColor.Yellow  // (255, 255,   0)
OverlayColor.Orange  // (255, 165,   0)
OverlayColor.Red     // (255,   0,   0)
OverlayColor.Green   // (  0, 220,   0)
OverlayColor.Cyan    // (  0, 255, 255)
OverlayColor.Gray    // (180, 180, 180)

// Custom RGB
new OverlayColor(r: 128, g: 0, b: 255)
```

#### Events
```csharp
// Fired when streaming state changes (fires only on actual false→true / true→false transitions)
public static event EventHandler<bool>? OnStreaming;

// Fired when client list changes
public static event Action<List<Client>>? OnClientsChange;

// Fired when new frame is available from back camera (for general purpose use: snapshots, processing, etc.)
public static event EventHandler<FrameEventArgs>? OnNewBackFrame;

// Fired when new frame is available from front camera (for general purpose use: snapshots, processing, etc.)
public static event EventHandler<FrameEventArgs>? OnNewFrontFrame;
```

### BaluLogger — In-Process Log Subscription

`BaluLogger` is a static wrapper around `Android.Util.Log` that simultaneously writes to Android logcat **and** fires a `static event` so consuming apps can receive every internal library diagnostic message in-process — no ADB or log-parsing required.

> Subscribe **before** creating a `Server` instance to capture startup and encoder-selection messages.

#### API

```csharp
// Log levels
public enum BaluLogLevel { Debug, Info, Warn, Error }

// Event args — all fields are set at construction time (immutable after firing)
public sealed class BaluLogEventArgs : EventArgs
{
    public BaluLogLevel Level    { get; }
    public string       Tag      { get; }
    public string       Message  { get; }
    public DateTime     Timestamp { get; }
}

// Static event — raised synchronously on the thread that produced the entry
public static event EventHandler<BaluLogEventArgs>? BaluLogger.OnLog;
```

#### Quick Start

```csharp
// Subscribe once at app startup, before creating Server
BaluLogger.OnLog += (_, e) =>
{
    // Forward to your own logging infrastructure
    _logger.LogInformation("[Balu/{Level}] {Tag}: {Message}",
        e.Level, e.Tag, e.Message);
};

var server = new Server(config);
server.Start();
```

#### Buffered / async handler (recommended for high-frequency use)

The event fires **synchronously** on the calling thread (encoder thread, camera callback, etc.). Keep handlers short — offload heavy work to a `Channel`:

```csharp
private readonly Channel<BaluLogEventArgs> _logChannel =
    Channel.CreateBounded<BaluLogEventArgs>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });

// Subscribe
BaluLogger.OnLog += (_, e) => _logChannel.Writer.TryWrite(e);

// Drain in background
_ = Task.Run(async () =>
{
    await foreach (var e in _logChannel.Reader.ReadAllAsync())
    {
        await _remoteLogger.SendAsync($"[{e.Level}] {e.Tag}: {e.Message}");
    }
});
```

#### Notes

- The event handler is wrapped in a `try/catch` inside `BaluLogger` — a subscriber crash never propagates to the calling thread.
- `BaluLogger.Debug` calls still appear in logcat but `Debug` entries are invisible in release builds on Android (`Log.Debug` is stripped by the build system). Subscribe to `OnLog` to capture them in both configurations.
- All 264+ `Log.X()` calls across the library route through `BaluLogger`, so every module (encoder, transport, streaming controller, camera services, RTSP protocol) is covered.

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
    string? certificatePassword = null,          // Certificate password
    int maxFrameRate = 30                         // Maximum FPS per client (frame rate limiting)
)
```

#### Methods
```csharp
// Start the MJPEG HTTP server
public void Start(bool StartWithoutStream = false)

// Stop the server
public void Stop()

// Get latest back camera JPEG frame (for snapshot endpoints)
public byte[]? GetLatestBackFrame()

// Get latest front camera JPEG frame (for snapshot endpoints)
public byte[]? GetLatestFrontFrame()
```

#### Properties
```csharp
// Connected client counts
public int ClientCount { get; }       // Total connected clients
public int BackClientCount { get; }   // Back camera clients
public int FrontClientCount { get; }  // Front camera clients

// Real-time FPS tracking
public double BackCameraFps { get; }  // Current back camera FPS
public double FrontCameraFps { get; } // Current front camera FPS

// Total frame counters
public long TotalBackFrames { get; }  // Total back camera frames processed
public long TotalFrontFrames { get; } // Total front camera frames processed
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

The H.264 encoder automatically optimizes for MediaTek and other Android devices:

```csharp
// The encoder is automatically configured when streaming starts
// Default settings:
// - Bitrate: AUTOMATIC — scaled to resolution at ~0.2 bits/pixel/frame (since v1.5.29)
//            e.g. 1280x720 -> ~5.5 Mbps, 1920x1080 -> ~12.4 Mbps, 2560x1440 -> ~22 Mbps
//            (clamped to [1.5 Mbps, 20 Mbps]). 0.2 bpp (raised from 0.1) gives motion-content
//            headroom so fast camera movement doesn't starve into macroblock smear. Can be
//            overridden manually — see below.
// - Frame rate: 30 FPS (since v1.5.29 — matches the camera sensor's measured delivery rate; was 25)
// - Profile: Main when the hardware encoder supports it, otherwise Baseline (since v1.5.29).
//            Main adds CABAC + better rate-distortion, so each bit yields a sharper picture.
// - Keyframe interval: 5 seconds (KeyFrameIntervalSeconds), set via SetInteger not SetFloat
//   (critical for MediaTek). New/reconnecting clients get an immediate IDR via RequestKeyFrame(),
//   so the interval never delays stream start. IDRs cost 80-220ms to encode on MT6768, so a
//   modest count keeps frame pacing smooth.
// - Intra-refresh: when the hardware encoder advertises FEATURE_IntraRefresh, a rolling intra-MB
//   refresh (KEY_INTRA_REFRESH_PERIOD ~1s) is enabled so a decoder converges without waiting for
//   the next full IDR. Guarded — encoders lacking the feature (e.g. MT6768's c2.mtk.avc) skip it
//   and rely on the IDR backstop, so it is safe everywhere.
// - Pipeline depth: MediaCodec's natural depth (a few frames in flight). KeyLatency=0 was
//   removed in v1.5.29 — it serialized the hardware encoder to ONE frame in flight, capping
//   throughput at 1/encode-time (~11fps at 2K on MT6768) regardless of CPU headroom.
// - Presentation timestamps: camera SENSOR_TIMESTAMP converted ns -> µs (since v1.5.29; see
//   MediaTek notes — feeding raw nanoseconds forced an IDR on every frame)
// - RTP timestamps: Wall-clock based (Stopwatch) for cross-SoC reliability

// Runtime RTCP adaptation may reduce the bitrate on real packet loss, clamped to
// [ceiling/2, ceiling] where the ceiling is the configured auto/manual value (since v1.5.29):
// the half-ceiling floor stops a transient loss spike from starving motion into blocky smear,
// and recovery back to the ceiling is quick (+25% / 3s). Disable with AdaptiveBitrate=false to
// pin the bitrate entirely — see below.
```

#### Bitrate Configuration (Auto & Manual)

Before v1.5.29 the bitrate was hardcoded to 2 Mbps regardless of resolution. At high resolutions this starves the encoder — e.g. 2 Mbps at 2560×1440@25 is only ~0.022 bits/pixel, far below the ~0.08–0.12 bpp needed for a clean picture. The result is a soft, smeared image that **raising the bitrate from a client could not fix**, because the value never reached the encoder (only RTCP congestion control could change it at runtime, and on a LAN it never raises the rate). Since v1.5.29 the bitrate is **auto-scaled to the resolution**, with an optional **manual override**.

**Motion quality (v1.5.29).** Even with auto-scaling, fast camera motion showed macroblock smearing while static scenes stayed sharp — the signature of rate-control starvation (static regions reuse the high-quality reference frame; moving regions need fresh residual bits the budget couldn't supply). Two things were fixed:

1. **Headroom.** The auto target was raised from ~0.1 to ~0.2 bits/pixel/frame (e.g. 1080p@30 → ~12.4 Mbps), so motion frames have bits to spend.
2. **Adaptive bitrate no longer anchors low.** Every client used to start at a 2 Mbps RTCP operating point with a 4 Mbps recovery ceiling — below the configured target — so the encoder was dragged to 2–4 Mbps and (on a clean LAN, with no loss to trigger recovery) effectively stuck there. Now `VideoProfile.MaxBitrate` defaults to 20 Mbps, the applied RTCP target is clamped to `[ceiling/2, ceiling]` (so it can never starve below half the configured value), the loss reaction is gentler (×0.85 instead of ×0.6), and recovery is faster (+25% every 3 s instead of +10% every 10 s).

```csharp
// AUTO (default): bitrate is derived from the configured resolution. No action needed.

// MANUAL: set an exact bitrate (bits/sec). Honored verbatim — even below the auto
// recommendation — so you can deliberately run a smaller stream (e.g. 2 Mbps at 2K).
server.SetBackCameraBitrate(8_000_000);   // back camera: exactly 8 Mbps
server.SetFrontCameraBitrate(4_000_000);  // front camera: exactly 4 Mbps

// Return a camera to automatic (resolution-scaled) bitrate:
server.SetBackCameraAutoBitrate();
server.SetFrontCameraAutoBitrate();

// Query the effective bitrate currently in use (manual if set, otherwise auto):
int back  = server.GetBackCameraBitrate();
int front = server.GetFrontCameraBitrate();

// Compute the recommended auto bitrate for a resolution (useful as a UI slider default):
int suggested = Server.GetRecommendedBitrate(2560, 1440);  // -> 20_000_000 (@30fps, ~22 Mbps clamped to AutoBitrateMax)
```

**Behavior notes:**
- **Manual values are not clamped up.** Only a broad hardware-safety clamp of `[100 kbps, 100 Mbps]` is applied, so values below the auto recommendation (such as 2 Mbps at 2K) are allowed and used exactly.
- A manual change is applied **live** if the encoder is running (via `MediaCodec.SetParameters`) and **persists across encoder restarts** (stall recovery, resolution change).
- Passing `0` (or any value `<= 0`) to `SetBackCameraBitrate`/`SetFrontCameraBitrate` selects automatic mode — identical to calling the `...AutoBitrate()` helpers.
- The configured value (auto or manual) is the **ceiling** for RTCP adaptive bitrate; on real packet loss adaptation moves only within `[ceiling/2, ceiling]` — never below half the target, so motion can't collapse into blocks — and recovers back to the ceiling quickly. `AdaptiveBitrate = false` pins the bitrate and ignores RTCP entirely.

| Resolution @30fps | Auto bitrate (~0.2 bpp)   |
|-------------------|---------------------------|
| 640×480 (VGA)     | ~1.8 Mbps                 |
| 1280×720 (HD)     | ~5.5 Mbps                 |
| 1920×1080 (FHD)   | ~12.4 Mbps                |
| 2560×1440 (QHD/2K)| 20 Mbps (clamped from ~22)|
| 3840×2160 (4K)    | 20 Mbps (ceiling)         |

#### Adaptive Bitrate (RTCP) — and how to pin it (v1.5.29)

The server can adapt the encoder bitrate at runtime based on RTCP receiver reports (packet loss / jitter) from the client. This is controlled by `ServerConfiguration.AdaptiveBitrate` (default `true`) and `Server.SetAdaptiveBitrate(bool)`.

```csharp
// Disable adaptation entirely — pin the encoder to its configured auto/manual bitrate.
var config = new ServerConfiguration { AdaptiveBitrate = false /* ... */ };

// Or toggle at runtime:
server.SetAdaptiveBitrate(false);          // pin to configured bitrate, ignore RTCP
bool on = server.IsAdaptiveBitrateEnabled();
```

**How adaptation behaves (when enabled):**
- The configured bitrate (auto-scaled or manual) is the **ceiling**. On a clean link the bitrate holds at exactly that value.
- On packet loss the encoder bitrate is reduced, but only within `[ceiling/2, ceiling]` — it can never starve below half the configured value, so motion stays out of macroblock breakup. Reduction is gentle (×0.85 on heavy loss, ×0.95 on mild) and recovery back to the ceiling is fast (+25% every 3 s).
- When **disabled**, RTCP reports are ignored and the encoder stays pinned to the configured bitrate. Recommended when you set the bitrate manually and want it honored verbatim, or on a controlled LAN.

> **Fixed in v1.5.29 — "stream is clean at first, then gets noisy after a few seconds."**
> Previously the per-client RTCP state defaulted to **2 Mbps** (`Client.CurrentBitrate`) with a **4 Mbps** cap (`VideoProfile.MaxBitrate`). The first receiver report would call `UpdateBitrate(...)` and **drag the encoder down** from its configured value (e.g. ~12 Mbps at 1080p) to 2–4 Mbps within seconds — so the picture started clean and then degraded into blocky smear on motion, and a manually chosen bitrate was silently overridden. Fixed by three coordinated changes: the RTCP operating point is aligned to the configured bitrate at stream start, `VideoProfile.MaxBitrate` now defaults to 20 Mbps so recovery can reach the target, and — as a robust backstop should the start-time alignment ever miss — the applied bitrate is clamped to `[ceiling/2, ceiling]` at the point `UpdateBitrate` is called, so it can never starve below half the configured value. See [Troubleshooting](#h264-stream-is-clean-at-first-then-becomes-noisy-after-a-few-seconds).

**MediaTek Device Notes:**
- The encoder uses `SetInteger(KeyIFrameInterval, N)` instead of `SetFloat()`. MediaTek MT6768 (and possibly other MediaTek SoCs) misinterprets sub-second float values as `0`, causing every frame to become an IDR keyframe. This exhausts the encoder's internal buffers after ~1000 frames and causes a permanent stall.
- **Presentation timestamps must be microseconds (root cause found in v1.5.29).** camera2 `SENSOR_TIMESTAMP` is **nanoseconds**; feeding it into MediaCodec unconverted makes consecutive frames appear 1000× further apart (40ms → 40 apparent seconds). The MediaTek C2 encoder's *time-based* GOP logic then sees "last IDR > sync-frame-interval ago" on **every frame** and forces all-IDR output — ~190KB per frame, 2× bitrate overshoot, and 80–220ms IDR encode times that capped 2K at ~7–11fps. `H264EncoderManager.FeedFrame` now converts ns → µs. This was also the true origin of the long-standing "MT6768 reports `PresentationTimeUs` in units ~1000x larger" observation: the encoder simply echoes input PTS on output.
- RTP timestamps are still derived from `Stopwatch` wall-clock time rather than encoder PTS — kept as defense-in-depth so RTP pacing stays correct regardless of upstream timestamp units or camera clock domains.
- The encoding loop drains output buffers before feeding new input to prevent buffer starvation on resource-constrained SoCs.
- `KeyLatency=0` must NOT be set (removed in v1.5.29): it forces a single frame in flight through the hardware encoder, so throughput collapses to `1 / hw_encode_time` (~11fps at 2560×1440 on MT6768) even though the camera delivers 30fps. MediaCodec's natural pipeline depth costs only 1–2 frame intervals of latency and restores camera-rate throughput.
- Camera frames are marshalled into a reusable 6-slot ring buffer via `JNIEnv.CopyArray` (v1.5.29). The previous per-frame `VideoFrame.GetData()` allocated a fresh 7.4MB managed array per frame at 2K30 (~220 MB/s of LOS churn), triggering ~16 explicit GC-bridge collections per second that throttled the entire pipeline.
- The encoding loop uses a persistent `SpinWait` (declared outside the loop) that escalates from spinning → yielding → sleeping as idle time accumulates. Resetting it on each productive iteration ensures the next idle period starts fresh. This avoids both `Thread.Sleep(1)` jitter (1–15 ms on Android) and the pure busy-spin of a per-iteration `new SpinWait()` (which never escalates past level 0).
- All pipeline channels (camera → encoder input → per-client output) use capacity=1 with `DropOldest` to minimize buffering latency. This ensures the encoder always processes the freshest camera frame.

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

| Use Case | Recommended Resolution | Auto Bitrate @30fps | Notes |
|----------|----------------------|---------------------|-------|
| Low bandwidth / Mobile data | QVGA (320x240) or Low (480x360) | 1.5 Mbps (floor) | Minimal data usage |
| Standard streaming | VGA (640x480) | ~1.8 Mbps | Default, most compatible |
| High quality local network | HD (1280x720) | ~5.5 Mbps | Good balance |
| Professional quality | Full HD (1920x1080) | ~12.4 Mbps | Requires powerful device |
| Ultra-high quality | QHD/2K (2560x1440) | 20 Mbps (clamped from ~22) | High-end devices only |
| Maximum quality | 4K UHD (3840x2160) | 20 Mbps (ceiling) | Flagship devices, high bandwidth required |

**Important:** The H.264 encoder buffer size is calculated as `(width * height * 3) / 2` for YUV420 format. Higher resolutions significantly increase memory usage. The library uses dynamic buffer management to prevent OOM crashes at high resolutions.

**Bitrate scales with resolution automatically (since v1.5.29).** When you change resolution, the auto bitrate follows (see [Bitrate Configuration](#bitrate-configuration-auto--manual)). If you previously relied on the fixed 2 Mbps default, note that higher resolutions now use proportionally more bandwidth by default — set a manual bitrate if you need to cap it.

### Frame Capture for Snapshots and Processing

The server provides general-purpose frame events that can be used for snapshots, image processing, or custom applications. These events work independently of the streaming functionality.

> **⚠️ Buffer lifetime (v1.5.29+):** `FrameEventArgs.Data` comes from a small reusable ring buffer (6 slots) in the camera service — this eliminates a multi-MB managed allocation per frame that previously caused a GC storm at high resolutions. The array contents are overwritten roughly 6 frames later (~200ms at 30fps). Using the data synchronously inside your event handler (as in the examples below) is always safe; if you keep frame data for later use, **make your own copy** (`frameArgs.Data.ToArray()` or `Buffer.BlockCopy` into your own buffer).

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

### ONVIF Profile S (v1.6.0+)

Make the device behave as a standard ONVIF camera so it auto-discovers into any VMS/NVR/ONVIF client (Milestone, Synology Surveillance Station, Blue Iris, ONVIF Device Manager, etc.). ONVIF is **opt-in** — when `ServerConfiguration.Onvif` is `null` (the default) nothing changes.

```csharp
var config = new ServerConfiguration
{
    Port = 7778,                                  // existing RTSP port — ONVIF advertises this
    Users = new() { ["admin"] = "password" },     // reused for ONVIF WS-UsernameToken auth
    AuthRequired = true,

    Onvif = new OnvifOptions
    {
        Enabled = true,
        Port = 8090,                              // ONVIF SOAP/HTTP port
        Manufacturer = "Balu",
        Model = "BodyCam-X1",
        FirmwareVersion = "1.6.0",
        SerialNumber = "BC-0001",                 // give each device a unique serial
        HardwareId = "balu-bodycam-1",
    },
};

var server = new Server(config);
server.Start();   // starts the ONVIF service + WS-Discovery announce alongside RTSP/MJPEG
```

**What it exposes:**
- **Device service** — `http://<device-ip>:8090/onvif/device_service` (GetSystemDateAndTime, GetDeviceInformation, GetCapabilities, GetServices, GetScopes).
- **Media service** — `http://<device-ip>:8090/onvif/media_service` (GetProfiles, GetStreamUri, GetVideoSources, GetVideoEncoderConfigurations, GetSnapshotUri). One profile per enabled camera (`Profile_back`, `Profile_front`).
- **GetStreamUri** returns the existing RTSP URL (e.g. `rtsp://<ip>:7778/live/back`); **GetSnapshotUri** returns a JPEG still (e.g. `http://<ip>:8089/snapshot/back.jpg`).
- **WS-Discovery** — the device answers Probes on `239.255.255.250:3702` and sends Hello/Bye so VMS systems find it automatically.

**Notes:**
- **Permission**: WS-Discovery needs `android.permission.CHANGE_WIFI_MULTICAST_STATE` in the host manifest to *receive* Probes (see [Required Permissions](#-required-permissions)). Without it the SOAP service still works if the client is pointed at the URL manually.
- **Auth**: ONVIF uses WS-Security UsernameToken (PasswordDigest), validated against the same `Users` as RTSP. `GetSystemDateAndTime` is always reachable unauthenticated so clients can sync their clock before building the digest. When `AuthRequired = false`, all ONVIF calls are open.
- **Snapshot endpoint**: `GET /snapshot/back.jpg` / `/snapshot/front.jpg` on the MJPEG port (8089) returns a single cached JPEG; HTTP 503 until the first frame is encoded.
- **Scope**: this is Profile S (streaming) — no PTZ/Events/Imaging (the bodycams are fixed). HTTP only on the ONVIF port.

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
1. Open VLC (version 3.0+ with live555 support)
2. Go to Media → Open Network Stream
3. Enter: `rtsp://admin:password123@your-ip:7778/live/back`
4. Click Play

**VLC Tips:**
- For best reliability, use TCP transport: Tools → Preferences → All → Input/Codecs → Network → set "RTP over RTSP (TCP)" to "Always"
- On Linux, the snap version of VLC is recommended (`snap install vlc`) — some distro packages (Debian/Kali) are compiled without live555 RTSP support
- Stream playback starts instantly on first connect thanks to encoder pre-warming at SETUP time

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

#### VLC Cannot Connect or Shows No Video

**1. VLC compiled without live555 (Linux)**
- **Symptom**: VLC shows "satip" or "access_realrtsp" errors instead of connecting
- **Cause**: Some Linux distro packages (Debian, Kali) compile VLC with `--disable-live555`
- **Fix**: Install VLC via snap (`snap install vlc`) or download from [videolan.org](https://www.videolan.org/) which includes live555

**2. Connection timeout on first connect**
- **Symptom**: VLC says "unable to open MRL" on first attempt, works on retry
- **Cause**: H.264 encoder warm-up delay exceeds live555 timeout
- **Fix**: This is handled automatically since v1.5.17 — the encoder is pre-warmed at SETUP time. If you still experience this, ensure you're using the latest version

**3. Video plays but is garbled or green**
- **Symptom**: VLC connects and shows frames, but image is corrupted (green bottom half)
- **Possible causes**:
  - **NV21 UV plane offset**: MediaTek cameras may produce oversized buffers (e.g., 1843198 bytes for 1280x720). The UV plane starts at `width * height` (declared dimensions), NOT at the end of the full buffer. Reading UV from the wrong offset causes green corruption. Fixed in v1.5.20.
  - **Color format mismatch**: `COLOR_FormatYUV420Flexible` has undefined buffer layout for raw ByteBuffer writes. Use `COLOR_FormatYUV420SemiPlanar` (NV12) instead. Fixed in v1.5.20.
  - **SPS/PPS not delivered**: The server sends SPS/PPS before every keyframe and on first frame. Ensure your client requests a new DESCRIBE/SETUP/PLAY sequence rather than resuming a stale session.

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

#### H.264 Image Is Soft / Blurry / Low-Detail (Raising Bitrate "Does Nothing")

If the picture looks soft, smeared, or lacks fine detail — and increasing the bitrate from your client app appears to have no effect — the encoder is almost certainly **under-bitrated for the resolution**. This is an encode-time quality loss, so it is independent of the network (it happens before any byte is sent; a 2 ms LAN won't change it).

- **Symptom**: Mushy, low-detail image with no blocky/torn artifacts; bitrate changes from the client seem ignored.
- **Cause (pre-v1.5.29)**: The encoder bitrate was hardcoded to 2 Mbps regardless of resolution. At 2560×1440@25 that is only ~0.022 bits/pixel — far below the ~0.08–0.12 bpp needed for a clean image. Client-side bitrate changes were never wired to the encoder constructor; only RTCP congestion control could change the rate at runtime, and on a LAN it never raises it.
- **Fix (v1.5.29)**: Bitrate is now **auto-scaled to the resolution** (~0.2 bpp), and a **manual override** is exposed that is honored exactly. See [Bitrate Configuration](#bitrate-configuration-auto--manual). The encoder also prefers the **Main** profile over Baseline when supported, improving quality at the same bitrate.
- **Blur only on motion (static stays sharp)?** That is the same starvation seen specifically on moving content — static regions reuse the sharp reference frame, so only fresh motion residual degrades. It usually means the **RTCP adaptive layer has dragged the rate down** (see the next section), not the auto target itself. The auto bpp was raised 0.1 → 0.2 to give motion more headroom, and the adaptive floor (`[ceiling/2, ceiling]`) keeps motion from collapsing.
- **Quick fix from a client**: `server.SetBackCameraBitrate(Server.GetRecommendedBitrate(width, height));` or just `server.SetBackCameraAutoBitrate();`
- **Diagnostic**: In logcat look for the encoder start line — it now logs the effective bitrate and mode, e.g. `Starting H264 encoder: 1920x1080 @ 12441600bps (auto)` and `Using H.264 Main profile`.

#### H.264 Stream Is Clean at First, Then Becomes Noisy After a Few Seconds

If the stream looks correct for the first few seconds and then degrades into noise/artifacts (with the bitrate appearing to "settle" at a lower value), the **RTCP adaptive bitrate** layer is dragging the encoder down below its configured value.

- **Symptom**: Clean start, then progressively noisier after ~2–10 seconds; quality stabilizes at a visibly lower level than the first moment — most obvious as **blocky smear on motion** while static scenes still look fine.
- **Cause (pre-v1.5.29)**: The per-client RTCP state defaulted to `Client.CurrentBitrate = 2 Mbps` with a `VideoProfile.MaxBitrate = 4 Mbps` cap — below the configured target (e.g. ~12 Mbps at 1080p). The first RTCP receiver report fired `UpdateBitrate(...)` and the encoder was dragged to 2–4 Mbps; on a clean LAN (no loss to trigger recovery, slow +10%/10s climb) it stayed there. A manually selected bitrate was silently overridden the same way. At 2–4 Mbps, 1080p has too few bits for motion residual, so moving regions break into macroblocks.
- **Fix (v1.5.29)**: Three coordinated changes — (1) at stream start the RTCP operating point (`CurrentBitrate`, `VideoProfile.MaxBitrate`) is aligned to the configured bitrate; (2) `VideoProfile.MaxBitrate` defaults to 20 Mbps so recovery can reach the target; (3) the applied bitrate is clamped to `[ceiling/2, ceiling]` in `OnBitrateAdjustmentRequired` (a robust backstop if the start-time alignment misses), and the loss reaction is gentler/faster (×0.85 down, +25%/3s up). See [Adaptive Bitrate (RTCP)](#adaptive-bitrate-rtcp--and-how-to-pin-it-v1529).
- **To eliminate adaptation entirely** (e.g. on a controlled LAN, or to honor a manual bitrate verbatim): `server.SetAdaptiveBitrate(false);` or `new ServerConfiguration { AdaptiveBitrate = false }`.
- **Diagnostic**: In logcat, the `Output rate: <fps> fps, avg <KB>/frame, <Mbps> (target <fps> @ <bps>)` line (every ~5s) shows both the achieved rate and the current target — a target well below the configured value means RTCP is still reducing. With the floor in place the target should never sit below half the configured bitrate.

#### Low Frame Rate at High Resolution (Camera Delivers 30fps, Stream Shows Much Less)

If the stream frame rate is far below the camera rate at high resolutions (e.g. ~7–11fps at 2560×1440) while CPU and RAM look idle, three compounding causes were identified and fixed in v1.5.29:

**1. All-IDR output from nanosecond timestamps** *(the dominant cause)*
- Camera `SENSOR_TIMESTAMP` (ns) fed to MediaCodec (expects µs) → the MediaTek time-based GOP logic forces an IDR on **every frame**. IDRs cost 80–220ms each to encode on MT6768 → hard ~7–11fps ceiling, ~2× bitrate overshoot, uniform ~190KB frames.
- Fixed: ns → µs conversion in `H264EncoderManager.FeedFrame`.

**2. GC-bridge storm from per-frame allocations**
- `VideoFrame.GetData()` allocated a fresh 7.4MB managed array per frame (2K) — ~220 MB/s of large-object churn at 30fps, triggering ~16 explicit GC-bridge collections/second that stalled every pipeline thread.
- Fixed: 6-slot reusable ring buffer + `JNIEnv.CopyArray` in the camera services. **Note**: `FrameEventArgs.Data` is now valid for ~6 frames (~200ms at 30fps); event subscribers that retain frame data must copy it.

**3. Single-frame-in-flight hardware pipeline (`KeyLatency=0`)**
- Zero-frame pipeline depth serializes the hardware encoder: feed → wait for encode → drain → feed. Throughput = `1 / hw_encode_time` (~11fps at 2K) regardless of CPU headroom. Invisible at VGA where encode takes a few ms.
- Fixed: removed `KeyLatency=0`; MediaCodec's natural pipeline depth (a few frames) costs only 1–2 frame intervals of latency.

Also in v1.5.29 the encoder frame rate was raised from 25 to 30fps to match the camera sensor delivery rate, and the NV21→NV12 chroma swap on the encoder hot path was vectorized (SIMD 16-bit byte-reverse instead of a per-byte loop over ~1.8MB/frame).

**Diagnostics:**
- Encoder output rate: logcat `Output rate: X fps, avg Y KB/frame, Z Mbps (target ...)` every ~5s.
- Frame types: `ffprobe -show_entries frame=pict_type -read_intervals "%+8" <rtsp-url>` — expect mostly `P`.
- GC pressure: count `GC freed` lines in logcat — healthy is <2/s while streaming.
- Camera delivery rate (MediaTek): count `LMVDrv` lines per second from the `camerahalserver` process — one per sensor frame.

#### H.264 Stream Freezes After a Few Seconds

If the H.264 stream starts but freezes after a few seconds (while MJPEG continues working), check these common causes:

**1. All-IDR Output (MediaTek devices)**
- **Symptom**: Every encoded frame is a keyframe (IDR), encoder stalls after ~1000 frames or frame rate is capped far below the camera rate
- **Cause A**: Using `SetFloat(KeyIFrameInterval, value)` with sub-second values — MediaTek interprets this as `0` (every-frame IDR). The library uses `SetInteger` since v1.5.16.
- **Cause B (found in v1.5.29)**: Feeding camera2 `SENSOR_TIMESTAMP` (**nanoseconds**) into MediaCodec as the presentation timestamp (**microseconds expected**). Frames appear 1000× further apart, so the MediaTek C2 *time-based* GOP logic decides the sync-frame interval has expired on every frame and forces all-IDR — even when the configured GOP is correct in the driver. Fixed by the ns → µs conversion in `FeedFrame`.
- **Diagnostic**: `ffprobe -show_entries frame=pict_type -read_intervals "%+8" <rtsp-url>` — a healthy stream shows mostly `P` with occasional `I`. All-`I` means one of the causes above. Also: uniform large frame sizes (e.g. ~190KB each at 2K) and bitrate overshooting the target ~2× are signatures of cause B.

**2. RTP Timestamp Mismatch**
- **Symptom**: Stream appears frozen in VLC/ffplay, but encoder is producing frames
- **Cause**: Encoder output PTS not in microseconds. Root cause found in v1.5.29: the *input* timestamps were camera nanoseconds and MediaCodec echoes input PTS on output — this is what historically looked like "MT6768 reports PresentationTimeUs ~1000x larger". Fixed at the source (ns → µs in `FeedFrame`).
- **Fix**: Input timestamps converted since v1.5.29; additionally the library has used `Stopwatch` wall-clock time for RTP timestamp derivation since v1.5.16, which works regardless of encoder timestamp units
- **Diagnostic**: Check RTP timestamp deltas — at 30fps/90kHz they should be ~3,000 per frame. If they're ~3,000,000, timestamps are in wrong units somewhere

**3. Encoder Thread Safety**
- **Symptom**: Stream freezes after ~2 frames
- **Cause**: Concurrent JNI calls to MediaCodec from different threads
- **Fix**: All MediaCodec access (input and output) must be serialized on a single thread. Fixed since v1.5.15

**4. Client Lifecycle Issues**
- **Symptom**: Stream works briefly then stops, client appears disconnected
- **Cause**: Aggressive timeout settings or premature client cleanup
- **Fix**: The library uses graduated error counting (10 consecutive failures for TCP, 5 for UDP) and checks `IsPlaying` before marking clients as dead. Fixed since v1.5.16

#### Stream Shows a Dark "STREAM STALLED" Window

If a connected client briefly shows a black screen with **"STREAM STALLED / Reconnecting camera…"** and a counting-up timer, this is expected, intentional behavior (v1.5.30+) — not a bug.

- **What it means**: The camera stopped delivering frames (e.g. a sensor-privacy / device-owner camera toggle, a HAL hiccup, or a camera restart). Rather than starving the client until it disconnects, the server keeps the H.264/MJPEG session alive by feeding a synthetic dark placeholder frame, so the session survives the outage.
- **What happens next**: The watchdog re-opens the camera in the background; once real frames return, live video resumes **on the same session** with no reconnect required. The elapsed-seconds timer shows how long the camera has been down.
- **If it never clears**: The camera itself isn't recovering — check `BaluLogger`/logcat for camera-open failures, verify the camera isn't disabled by device-owner policy, and confirm the camera permission is still granted.
- **To disable it**: Set `ServerConfiguration.StallPlaceholderEnabled = false`. The stream will then stall and the client will eventually disconnect on its own (the pre-v1.5.30 behavior).
- **Distinct from a frozen last frame**: A *frozen* picture with **no** message is an encoder stall (camera still delivering), handled separately by restarting just the encoder — see the freeze section above.

#### App Crashes After Hours of Streaming (SIGABRT)

If the app crashes after hours of continuous streaming with `Cannot transition thread from RUNNING with DONE_BLOCKING` in the logs:

**Root Cause**: Mono GC thread-state corruption triggered by BufferQueue starvation. The camera's ImageReader buffer slots fill up because frames are not released fast enough, causing JNI calls to block for 1+ seconds. While blocked, the Mono GC tries to transition the thread state and hits an invalid state machine transition, aborting the process.

**Fix (v1.5.21)**: Camera services now marshal all Java `VideoFrame` data into managed `FrameEventArgs` in `OnFrameAvailable` (on the JNI callback thread) and recycle the native frame immediately. The processing thread (`ProcessFramesAsync`) operates entirely in managed code with zero JNI calls, eliminating the GC thread-state race window.

**Complementary native AAR fix (v2.0.2)**: The native Kotlin library now calls `Image.close()` immediately after copying pixel data, before invoking the .NET callback, so ImageReader buffer slots are never held during slow callback execution.

**Diagnostic**: Check logcat for `waitForFreeSlotThenRelock TIMED_OUT` on `ImageReader` — this indicates the BufferQueue is starved.

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

**Connection Stability Notes:**
- The server uses graduated error counting: TCP clients tolerate up to 10 consecutive send failures before being disconnected, UDP clients tolerate 5. This prevents premature disconnection from transient network issues.
- Playing clients are protected from the WatchDog — they are never marked as dead while actively streaming.
- Frame dequeue uses a 200ms timeout; a stall is declared after **5 seconds** (25 consecutive timeouts) — wide enough for slow/padded cameras (e.g. VGA on MediaTek at ~0.3 fps) without false restarts.
- Per-client `SemaphoreSlim` (SendLock) serializes all sends to prevent TCP interleaved framing corruption. Batch RTP sends acquire the lock once per frame instead of per packet.
- The WatchDog stops the H.264 encoder when no clients are connected, preventing an idle stall loop from consuming resources. The camera stays running for fast reconnects.
- Simultaneous client connects are race-free: `_isStreamingFlag` uses `Interlocked.CompareExchange` (v1.5.25) so `CameraStartRequested` and `StreamingStateChanged(true)` are each fired exactly once, even when two clients arrive at the same millisecond.
- `OnStreaming` fires only on real state transitions (v1.5.25): the WatchDog previously fired `OnStreaming(true)` unconditionally every 5 seconds while cameras were running, flooding consumer apps with spurious events. A `_lastReportedStreamingState` guard now suppresses duplicate fires.
- `Stop()` fully tears down cameras and resets state (v1.5.25): `StopCapture()` is now called on both camera services so the AAR's camera2 session is fully torn down and recreated on the next `Start()`. Without this, a hung camera2 session persisted across watchdog restarts and `RestartCameraForStallRecovery` could not recover it. Client list is also cleared on `Stop()` to prevent stale entries appearing in the next session's WatchDog.
- `SetStreamingState` fires only on first SETUP per session (v1.5.25): `PreStartCameraAndEncoder` now guards the `SetStreamingState(true)` call with `if (!_isStreaming)`, preventing a duplicate `[STREAMING] ACTIVE` event for every subsequent SETUP request.

**Latency Optimization Notes (v1.5.23–v1.5.25):**
- All frame channels use capacity=1 with `DropOldest` — the encoder always processes the freshest frame, eliminating queue-induced latency.
- The H.264 streaming loop is fully **synchronous**: `WaitDequeueFrame` blocks the OS thread with a kernel futex (~1 ms wake latency) instead of async continuation scheduling (10–70 ms on Android's busy thread pool). `SendBatchSync` uses a blocking `socket.Send()` call.
- RTP packets for an entire H.264 frame are built into a batch and sent with a single `socket.Send()` call (TCP), reducing per-frame network overhead from 10–15 syscalls to 1.
- The encoder loop uses a persistent `SpinWait` (v1.5.25) that properly escalates from spinning to yielding to sleeping across idle iterations, preventing CPU burn between frames.
- Per-client channel references are cached at session start — the per-frame dictionary lock that was acquired on every `TryDequeueFrame`/`WaitDequeueFrame` call is eliminated entirely (v1.5.25).
- H.264 encoder input buffer zeroing is now selective: only stride-gap padding bytes are cleared, not the entire 1.4 MB buffer. The hot path (camera res == encoder res) performs zero `Array.Clear` calls per frame (v1.5.25).
- Fan-out to client channels snapshots the channel list under the lock then writes outside the lock, so registering/unregistering clients does not wait for all `TryWrite` calls to complete (v1.5.25).
- Typical server-side path: **queue ≈ 0.2–0.6 ms · build ≈ 0.2–0.4 ms · send ≈ 1–3 ms**.

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

### Completed (v1.1-v1.5.16)
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
- ✅ **Connection stability and timeout improvements** (v1.5.9)
- ✅ **Event-driven architecture and shared JPEG encoding** (v1.5.10)
- ✅ **Continuous streaming mode** (v1.5.11)
- ✅ **Native library frame delivery fix** (v1.5.12)
- ✅ **MJPEG streaming smoothness with per-client frame pacing** (v1.5.13)
- ✅ **Client reconnection bug fix** (v1.5.14)
- ✅ **H.264 thread safety fix and connection stability** (v1.5.15)
- ✅ **H.264 stream freeze fix — MediaTek I-frame interval, RTP timestamps, client lifecycle** (v1.5.16)
- ✅ **VLC compatibility — RFC-compliant RTSP/SDP, CRLF line endings, encoder pre-warming** (v1.5.17)
- ✅ **H.264 green corruption fix — NV21 UV plane offset, NV12 color format, resolution change support** (v1.5.20)
- ✅ **Long-running stability fix — JNI-free processing thread, immediate native frame release, bitrate fix, latency reduction** (v1.5.21)
- ✅ **Ultra-low latency pipeline — buffer depth reduction, batch RTP sends, allocation elimination, SpinWait encoder loop** (v1.5.23)
- ✅ **Text overlay burn-in — configurable multi-slot YUV-level text stamped into video frames** (v1.5.24)
- ✅ **Synchronous streaming hot path — WaitDequeueFrame + SendBatchSync eliminate async scheduling latency** (v1.5.24)
- ✅ **Thread-safety hardening — volatile flags, volatile backing fields for IsPlaying/ConsecutiveSendErrors, missing lock fix** (v1.5.24)
- ✅ **Idle encoder stall loop fix — WatchDog stops encoder when no clients connected; wider stall timeout for slow cameras** (v1.5.24)
- ✅ **Hot-path CPU & memory optimisation + simultaneous-connect race fix — SpinWait escalation, cached channel refs, selective buffer zeroing, fan-out lock reduction, atomic CAS start gate** (v1.5.25)
- ✅ **Server restart hardening — Stop() tears down cameras + clears clients, OnStreaming fires only on state change, SetStreamingState guarded on SETUP** (v1.5.25)
- ✅ **In-process log subscription — BaluLogger.OnLog exposes all 264+ internal log calls as a subscribable event (tag, level, message, timestamp)** (v1.5.25)
- ✅ **AAC audio streaming — optional second m=audio RTSP track (trackID=1) carrying hardware-encoded AAC-LC via RFC 3640 mpeg4-generic; backward compatible (flag-gated, off by default)** (v1.5.28)
- ✅ **Resolution-scaled bitrate + manual override — auto bitrate at ~0.2 bpp (fixes soft/blurry high-res image from the old hardcoded 2 Mbps; raised from 0.1 bpp to stop motion macroblock smear), per-camera manual override honored exactly, Main profile when supported** (v1.5.29)
- ✅ **Adaptive-bitrate fix + toggle — RTCP no longer starves the encoder: operating point aligned to the configured bitrate at start, `MaxBitrate` ceiling raised 4→20 Mbps, applied rate clamped to `[ceiling/2, ceiling]` as a backstop (fixes "clean at first, then blocky smear on motion"), gentler/faster reaction (×0.85 down, +25%/3s up), `AdaptiveBitrate` config flag + `SetAdaptiveBitrate()` to pin it, output-FPS diagnostic log** (v1.5.29)
- ✅ **All-IDR fix — camera SENSOR_TIMESTAMP (ns) now converted to µs before MediaCodec; raw ns made the MediaTek time-based GOP logic force an IDR on every frame (~190KB frames, 2× bitrate overshoot, ~7–11fps cap at 2K). Also the true origin of the historical "MT6768 PTS 1000× larger" workaround** (v1.5.29)
- ✅ **High-resolution frame-rate unlock — pooled 6-slot frame ring + JNIEnv.CopyArray kills the per-frame 7.4MB managed allocation and its 16-GCs/sec bridge storm; KeyLatency=0 removed to restore hardware encoder pipelining; NV21→NV12 chroma swap vectorized (SIMD); encoder raised to 30fps to match the camera sensor** (v1.5.29)
- ✅ **Low-latency intra-refresh — rolling intra-MB refresh (`KEY_INTRA_REFRESH_PERIOD` ~1s) enabled when the codec advertises `FEATURE_IntraRefresh`, so a decoder converges without waiting for the next full IDR; guarded no-op on encoders lacking it (e.g. MT6768's c2.mtk.avc), so it is safe everywhere** (v1.5.29)
- ✅ **Stall resilience — dark "STREAM STALLED" placeholder keeps the client session alive while a stalled camera recovers (synthetic black frame + message + live elapsed timer fed to H.264/MJPEG, real video resumes on the same session); encoder-stall vs camera-stall distinction restarts just the hung encoder (~1s) instead of the whole camera (10–30s); `StallPlaceholderEnabled` config flag, default on** (v1.5.30)
- ✅ **ONVIF Profile S — opt-in WS-Discovery + Device/Media SOAP services so the device drops into any VMS/NVR/ONVIF system; GetStreamUri returns the existing RTSP URL, GetSnapshotUri returns a JPEG still; WS-UsernameToken auth reuses the RTSP user store; protocol logic kept Android-free and unit-tested; `ServerConfiguration.Onvif`, off by default** (v1.6.0)

### Planned (v1.6+)
- ⬜ Fix image rotation on some devices
- ⬜ Add H.265 (HEVC) codec support
- ⬜ Add integration tests for Android-dependent code
- ⬜ RTCP Sender Reports for the AAC audio track + explicit lip-sync hardening between video and audio
- ⬜ Per-camera audio source selection (currently one shared microphone)

### Long Term (v2.0+)
- ⬜ iOS support via .NET MAUI
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

- v1.6.0: **ONVIF Profile S support** — the device now behaves as a standard ONVIF camera, so it auto-discovers into any VMS/NVR/ONVIF client and is queried for its streams over SOAP. Opt-in and backward compatible: gated on `ServerConfiguration.Onvif` (default `null`/disabled), so existing deployments are unchanged. Adds only the discovery + description layer — the RTSP/H.264 media pipeline is untouched.

  **Discovery (`Services/Onvif/WsDiscoveryService.cs` + `WsDiscoveryMessages.cs`):**
  - Joins multicast `239.255.255.250:3702`, answers client `Probe` with a unicast `ProbeMatch`, and announces the device with `Hello` on start / `Bye` on stop. Advertises types `dn:NetworkVideoTransmitter tds:Device` and the streaming scopes.
  - The device's WS-Discovery UUID is derived from `SerialNumber`/`HardwareId` so it stays stable across restarts.
  - Android note: receiving Probes requires a `WifiManager.MulticastLock` and `android.permission.CHANGE_WIFI_MULTICAST_STATE` in the host manifest. The lock is acquired/released automatically; without the permission, outbound Hello/ProbeMatch still go out but inbound Probes are dropped by the OS.

  **SOAP service (`Services/Onvif/OnvifServer.cs`):**
  - `HttpListener` on port 8090 (configurable) serving `/onvif/device_service` and `/onvif/media_service`, mirroring the `MjpegServer` listener pattern (including the Android `0.0.0.0`→`*` prefix fix).
  - Device: `GetSystemDateAndTime`, `GetDeviceInformation`, `GetCapabilities`, `GetServices`, `GetServiceCapabilities`, `GetScopes`. Media: `GetProfiles`/`GetProfile`, `GetVideoSources`, `GetVideoSourceConfigurations`, `GetVideoEncoderConfigurations`/`GetVideoEncoderConfiguration`, `GetStreamUri`, `GetSnapshotUri`. One profile per enabled camera (`Profile_back`, `Profile_front`) built from the live encoder resolution and bitrate.

  **Auth (`Services/Onvif/OnvifSecurity.cs`):**
  - WS-Security UsernameToken `PasswordDigest` (`Base64(SHA1(nonce + created + password))`) and `PasswordText`, validated against the existing RTSP `Users` store — no new credential state. `GetSystemDateAndTime` is always reachable unauthenticated (PRE_AUTH) so clients can sync their clock first. Gated on `ServerConfiguration.AuthRequired`.

  **Snapshot (`Services/MjpegServer.cs`):**
  - New `/snapshot/back.jpg` + `/snapshot/front.jpg` endpoint on the MJPEG port returns a single cached JPEG (503 until the first frame is encoded). Referenced by ONVIF `GetSnapshotUri`.

  **Architecture & testing:**
  - All SOAP/XML/digest/discovery *logic* lives in Android-free classes (`OnvifSoap`, `OnvifSecurity`, `OnvifDeviceService`, `OnvifMediaService`, `WsDiscoveryMessages`); only the transport shells touch the platform. The logic is linked into the test project and covered by unit tests (SOAP parsing, a known-answer PasswordDigest vector, GetStreamUri/GetProfiles composition, ProbeMatch content).
  - New config: `ServerConfiguration.Onvif` (`OnvifOptions`: `Enabled`, `Port`, `Manufacturer`, `Model`, `FirmwareVersion`, `SerialNumber`, `HardwareId`).
  - Scope: Profile S (streaming) only — no PTZ/Events/Imaging; HTTP only on the ONVIF port.

- v1.5.30: **Stall Resilience — dark "STREAM STALLED" placeholder** — a stalled camera no longer drops the client. While the camera delivers no frames, the server keeps the H.264/MJPEG session alive by feeding a synthetic dark placeholder frame, and real video resumes seamlessly on the same session once the camera recovers. Gated on `ServerConfiguration.StallPlaceholderEnabled` (default `true`).

  **Placeholder rendering (`Services/StallPlaceholder.cs`):**
  - Renders a black NV21 frame with centered status text — `STREAM STALLED` (title) / `Reconnecting camera…` — plus a live `{n}s` elapsed-seconds timer, sized to the encoder resolution so it's legible at any resolution.
  - Only the Y (luma) plane is touched: white-on-black text is chroma-neutral, so the buffer keeps `U=V=128`. The text is re-rasterized only when the visible second changes (otherwise the same buffer is returned), so the per-frame cost during a stall is ~nil.

  **Keepalive pump (`RTSP/Server.cs`):**
  - A background `StallPump` task ticks every 200ms (~5fps, far under the ~5s starve window). When a camera has produced at least one real frame but none for >0.4s and a client is watching, it feeds the placeholder via `FeedSyntheticFrame` — the same fan-out as a real frame (H.264 encoder + JPEG/MJPEG + cached latest-frame + `OnNewFrame`).
  - Because the encoder keeps getting fed, the per-client streaming loop never reaches its starve/disconnect branch, so the session is never torn down. PTS is kept monotonic (shared +200ms steps with real frames) so resume doesn't glitch.
  - On connect-during-stall, the placeholder is published into the cached latest-frame so `WaitForFrameAndStartEncoder` succeeds and the encoder starts on the placeholder.

  **Camera-stall vs encoder-stall distinction (`RTSP/Streaming/StreamingController.cs`):**
  - **Camera stall** (camera stops delivering): the pump keeps the encoder fed, the loop never stalls, and the watchdog re-opens the camera in the background.
  - **Encoder stall** (camera still delivering, but the MediaTek encoder hangs): the pump doesn't engage, so the loop hits its stall branch and restarts the encoder. A new `IsCameraFresh` delegate distinguishes the two — a fresh camera frame (<1s) means an encoder-only restart (~1s) instead of a needless full camera restart (10–30s on MediaTek). The watchdog camera self-heal threshold was lowered 15s→8s since the placeholder now hides the wait.

  **Configuration:**
  - New `ServerConfiguration.StallPlaceholderEnabled` (bool, default `true`). Set `false` to restore the pre-v1.5.30 behavior (the stream stalls and the client eventually disconnects).

  **Verified:** captured the live H.264 over ffplay/ffmpeg during a `sensor_privacy` camera kill on a 1080p MediaTek device — encoder output never dropped to zero, the session never disconnected, the dark placeholder rendered with the elapsed counter incrementing, and real video resumed on the same session.

- v1.5.29: **High-Resolution Quality & Frame-Rate Overhaul** — fixes the chain of issues that made 2K streaming soft, noisy, and slow (~7fps); after this release the pipeline runs at the camera's native 30fps with resolution-appropriate bitrate.

  **Quality fixes:**
  - **Resolution-scaled auto bitrate** (`H264EncoderManager`): the hardcoded 2 Mbps constructor bitrate (≈0.022 bits/pixel at 2K — the cause of the soft, smeared image) is replaced by an automatic ~0.2 bits/pixel/frame calculation clamped to [1.5, 20] Mbps (1920×1080@30 → ~12.4 Mbps; 2560×1440 clamps to 20 Mbps). Raised from 0.1 → 0.2 bpp after fast motion showed macroblock smear while static scenes stayed sharp (rate-control starvation on motion residual). New API: `Server.SetBackCameraBitrate(int)` / `SetFrontCameraBitrate(int)` (manual override honored verbatim, even below the recommendation), `Set*AutoBitrate()`, `Get*Bitrate()`, static `GetRecommendedBitrate(w, h)`.
  - **RTCP adaptive-bitrate starvation fix** (`Server`, `StreamingController`, `RtcpManager`, `VideoProfile`): per-client RTCP state defaulted to 2 Mbps with a 4 Mbps cap — below the configured target — so the first receiver report dragged the encoder to 2–4 Mbps and (on a clean LAN) kept it there, showing as blocky smear on motion. Three coordinated fixes: the operating point is aligned to the configured bitrate at stream start; `VideoProfile.MaxBitrate` now defaults to 20 Mbps so recovery can reach the target; and the applied rate is clamped to `[ceiling/2, ceiling]` in `OnBitrateAdjustmentRequired` as a robust backstop (the start-time alignment can miss before the first report). The loss reaction was also softened (×0.85 vs ×0.6) and recovery sped up (+25%/3s vs +10%/10s). New `ServerConfiguration.AdaptiveBitrate` flag and `Server.SetAdaptiveBitrate(bool)` disable adaptation entirely (pin the bitrate).
  - **Low-latency intra-refresh** (`H264Encoder`): when the hardware encoder advertises `FEATURE_IntraRefresh`, `KEY_INTRA_REFRESH_PERIOD` (~1s of frames) is set so the decoder converges via a rolling intra-MB refresh instead of waiting for the next full IDR — faster join/recovery and no large periodic IDR bursts. Feature-gated and wrapped in try/catch, so encoders without it (e.g. MT6768's c2.mtk.avc) silently fall back to the IDR backstop.
  - **H.264 Main profile** (`H264Encoder`): used when the hardware encoder advertises support (recorded during encoder evaluation in `EncoderInfo.SupportsMainProfile`); falls back to Baseline. CABAC + better rate-distortion = sharper picture per bit. Compatible with the MediaTek SPS crop patch (which only bails on High/extended profiles).

  **Frame-rate fixes (7fps → camera rate at 2K):**
  - **Nanosecond→microsecond presentation timestamps** (`H264EncoderManager.FeedFrame`): camera2 `SENSOR_TIMESTAMP` (ns) was fed to MediaCodec (expects µs), making frames appear 1000× further apart. The MediaTek C2 encoder's time-based GOP logic saw the 30s sync-frame interval expire on *every frame* and forced all-IDR output: ~190KB/frame, ~2× bitrate overshoot, and 80–220ms IDR encode times that capped 2K at ~7–11fps. Also the true origin of the historical "MT6768 reports PresentationTimeUs ~1000x larger" RTP workaround — the encoder echoes input PTS.
  - **Pooled camera frame ring** (`BackCameraService`/`FrontCameraService`): `VideoFrame.GetData()` allocated a fresh 7.4MB managed array per frame (2K), ~220 MB/s of LOS churn at 30fps that triggered ~16 explicit GC-bridge collections/second and stalled every pipeline thread. Replaced with a 6-slot reusable ring filled via raw JNI (`JNIEnv.CallObjectMethod` + `JNIEnv.CopyArray`); steady-state allocates nothing. **Behavioral note**: `FrameEventArgs.Data` is reused ~6 frames later — event subscribers must copy retained frame data.
  - **Hardware encoder pipelining restored** (`H264Encoder.Start`): removed `KeyLatency=0` (zero-frame pipeline depth), which serialized the encoder to one frame in flight and capped throughput at 1/encode-time regardless of CPU headroom. MediaCodec's natural depth costs 1–2 frame intervals of latency and restores camera-rate throughput.
  - **Vectorized NV21→NV12 chroma swap** (`H264Encoder.WriteFrameToEncoderBuffer`): the per-byte V/U swap loop (~1.8MB/frame at 2K, ~15–20ms on one core) replaced with `BinaryPrimitives.ReverseEndianness` over `ushort` spans (SIMD 16-bit byte-reverse).
  - **Encoder frame rate 25 → 30fps** (`H264EncoderManager.EncoderFrameRate`): matches the camera sensor's measured delivery rate; configuring below it just dropped frames.

  **Diagnostics:**
  - Encoder start line now logs the effective bitrate and mode: `Starting H264 encoder: 2560x1440 @ 11059200bps (auto)`, plus `Using H.264 Main|Baseline profile`.
  - New rolling output-rate log every ~5s: `Output rate: X fps, avg Y KB/frame, Z Mbps (target Nfps @ Mbps)` — separates encoder-side fps problems from network/send-side ones.

- v1.5.28: **AAC Audio Streaming** — optional second RTSP track carrying hardware-encoded AAC-LC alongside the H.264 video stream. Backward compatible: the new behaviour is gated on `ServerConfiguration.EnableAudioTrack` (default `false`) so existing single-track clients see byte-identical SDP and no microphone is opened.

  **Protocol & SDP:**
  - `SdpGenerator.GenerateSdp` now appends a second `m=audio 0 RTP/AVP 97` block when audio is enabled: `a=rtpmap:97 mpeg4-generic/<sampleRate>/<channels>` plus RFC 3640 AAC-hbr `a=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;config=<ASC>;sizelength=13;indexlength=3;indexdeltalength=3` and `a=control:trackID=1`. The video block remains unchanged with `a=control:trackID=0`.
  - `RtspProtocolHandler.HandleSetupAsync` parses `trackID=N` from the SETUP URI. `trackID=0` (or absent) populates the existing video fields on `Client`; `trackID=1` populates a parallel set of `Audio*` transport fields without disturbing the video state. When audio is not enabled, `SETUP trackID=1` returns 404.
  - PLAY emits a multi-track `RTP-Info` header (`url=<base>/trackID=0;seq=...;rtptime=...,url=<base>/trackID=1;seq=...;rtptime=...`) when the audio track has been set up.

  **New Client state (RFC 3550 §5.1 — independent RTP streams):**
  - `Models/Client.cs` gains `AudioSetupComplete`, `AudioRtpChannel`, `AudioRtcpChannel`, `AudioRtpEndPoint`, `AudioRtcpEndPoint`, `AudioUdpSocket`, `AudioRtcpSocket`, `AudioSsrcId`, `AudioSequenceNumber`, `AudioRtpTimestamp`, `AudioBaseEncoderTimestamp`, `AudioBaseRtpTimestamp`. `Dispose()` closes the audio sockets.

  **Capture & encoder pipeline:**
  - `Services/AudioCaptureService.cs` — `Android.Media.AudioRecord` wrapper. Reads PCM-16 frames sized exactly to the AAC-LC access-unit length (1024 samples/channel) on a dedicated background thread. Timestamps are derived from a monotonic sample cursor, avoiding the MediaTek PTS-unit corruption observed on the video encoder. Checks `RECORD_AUDIO` before opening the device and raises `ErrorOccurred` instead of crashing when the permission is missing.
  - `RTSP/AacEncoder.cs` — `Android.Media.MediaCodec` wrapper for `audio/mp4a-latm` (AAC-LC). Background encoder thread driving `FeedInputBuffer` / `DrainOutputBuffer`, mirroring `H264Encoder.EncodingLoop`. Captures `csd-0` (AudioSpecificConfig) from `OutputFormatChanged` or the `CodecConfig` frame and surfaces it on the first `FrameEncoded` event so downstream SDP can sync.
  - `RTSP/Streaming/AacEncoderManager.cs` — single shared encoder fanned out to per-client bounded channels (capacity 4, `DropOldest`). Snapshot-then-write pattern matches `H264EncoderManager` so registering / unregistering a client never blocks the fan-out loop.

  **RTP packetisation:**
  - `RTSP/Transport/RtpPacketBuilder.BuildAacRtpPacket` — RFC 3640 `mode=AAC-hbr`, one access unit per RTP packet (no fragmentation; AAC AUs at typical rates are far below MTU). Layout: 12-byte RTP fixed header → 2-byte AU-headers-length (`0010` = 16 bits) → 2-byte AU header (`(size << 3) | au-index(0)`) → AAC bytes. Marker bit set (single AU per packet). Uses the audio-side `AudioSequenceNumber` / `AudioSsrcId` so video sequence space is untouched.
  - `RtpPacketBuilder.EncoderTimestampToAudioRtp` — converts the AAC PTS in microseconds into the audio sample-rate RTP clock with `(elapsedUs * sampleRate) / 1_000_000`. Establishes the baseline on the first call and increments from there.

  **Transport:**
  - `ITransportManager.SendAudioBatchSync` + `TransportManager.SendBatchSyncCore(..., isAudio)` route through `Client.AudioRtpChannel` (TCP interleaved) or `Client.AudioUdpSocket` + `Client.AudioRtpEndPoint` (UDP). Shares the per-client `SendLock` with video so TCP-interleaved 4-byte framing (`0x24 channel length payload`) stays atomic across the two tracks.
  - The interleaved batch helper is now parameterised by channel byte, so video continues using `Client.RtpChannel` and audio uses the new `AudioRtpChannel` field without any duplication.

  **Streaming orchestration:**
  - `StreamingController.StreamAudioToClientAsync` runs as a parallel `LongRunning` task per client, gated on `Client.AudioSetupComplete`. Dequeues encoded AAC frames, builds one RTP packet per access unit, sends via `SendAudioBatchSync`. Exits on `IsPlaying=false`, channel-closed, or cancellation. The constructor takes an optional `AacEncoderManager` — when null (audio disabled), the loop returns immediately.

  **Server lifecycle:**
  - `Server.cs` instantiates `AudioCaptureService` + `AacEncoderManager` only when `EnableAudioTrack` is true and wires `AudioCapture.FrameReceived → AacEncoderManager.QueueFrame` and `AacEncoderManager.FrameEncoded → SdpGenerator.UpdateAudioSpecificConfig` (so the SDP `config=` value tracks the actual hardware ASC).
  - `PreStartAudioPipelineIfNeeded` warms the AAC encoder + mic on first audio `SETUP` so PLAY can immediately emit packets.
  - `WatchDog` stops the audio pipeline when `ClientCount == 0` — mirrors the H.264 idle-stall policy and ensures the microphone isn't held open with no listeners.
  - `Server.Stop()` and `Dispose()` tear the audio pipeline down alongside cameras.

  **Configuration:**
  - New `ServerConfiguration` properties: `EnableAudioTrack` (bool, default `false`), `AudioSampleRateHz` (int, default 44100), `AudioChannels` (int, default 1). All three flow through to the param-based `Server` ctor.

  **Permissions:**
  - `android.permission.RECORD_AUDIO` must be declared in the host `AndroidManifest.xml` and granted at runtime when audio is enabled. If the runtime grant is missing, `AudioCaptureService.StartCapture` logs an error via `ErrorOccurred` and returns — the video pipeline keeps working, audio simply stays silent.

  **Tested with:** VLC and ffplay multi-track SDP parsing on `net9.0-android`, dual-track SETUP / PLAY exchange, AAC AU dispatch over both TCP interleaved and UDP transports.

- v1.5.25: **Hot-Path Optimisation, Server Restart Hardening, Simultaneous-Connect Race Fix, In-Process Logging**

  **Hot-path CPU & memory optimisations:**
  - **SpinWait escalation fix** (`H264Encoder.EncodingLoop`): the `SpinWait` is now declared once outside the encoding loop so its spin count accumulates across idle iterations and correctly escalates from spinning → yielding → sleeping. Previously a fresh `new SpinWait()` was created each iteration — it never advanced past level-0 spinning, burning 100% CPU during the 40 ms gaps between frames.
  - **Per-client channel caching** (`StreamingController`): the `Channel<H264FrameEventArgs>` reference is retrieved once after `RegisterClientChannel` and reused for the session, eliminating 50 dictionary lock acquisitions/second/client.
  - **Selective encoder buffer zeroing** (`H264Encoder.WriteFrameToEncoderBuffer`): replaced `Array.Clear(buf, 0, 1.4 MB)` with targeted clearing of only stride-gap padding bytes. The hot path (camera res == encoder res) now performs zero `Array.Clear` calls — eliminates 35 MB/s of unnecessary RAM writes at 1280×720 / 25 fps.
  - **Fan-out lock reduction** (`H264EncoderManager`): client channel list is snapshotted under the lock into a pooled array, then the lock is released before `TryWrite` calls. Unregistering a client no longer blocks all fan-out writes.
  - **Removed dead code**: deleted `ConvertNV21ToNV12Pooled` — it rented a pool buffer then immediately heap-allocated a copy, negating the pool entirely.
  - **`ProbeSupportedResolution` startup fix**: now calls `GetCachedBestEncoder()` instead of re-scanning all codecs, saving 200–700 ms per `Start()`.

  **Simultaneous-connect race fix:**
  - **Atomic streaming start gate** (`StreamingController`): replaced `volatile bool _isStreaming` + plain read/write with `volatile int _isStreamingFlag` guarded by `Interlocked.CompareExchange(ref _isStreamingFlag, 1, 0) == 0`. With the previous code, two clients arriving in the same scheduler timeslice could both read `_isStreaming == false` before either wrote `true`, causing `CameraStartRequested` and `StreamingStateChanged(true)` to fire twice — observed as double `[STREAMING] State changed: ACTIVE` in device logs. `SetStreamingState` updated to use `Interlocked.Exchange`.

  **Server restart hardening:**
  - **`Stop()` now tears down cameras** (`Server`): `StopCapture()` called on both camera services and `_isCapturingBack/_isCapturingFront` reset to `false`. Previously a hung Android camera2 session (the root cause of 1-hour stalls) persisted across watchdog Stop/Start cycles, making `RestartCameraForStallRecovery` ineffective — it was calling the same already-stuck service.
  - **`Stop()` clears client list**: `_clientManager.ClearAllClients()` called on stop so stale client entries from the previous session are not visible to the new session's WatchDog.
  - **`Stop()` resets streaming flag**: `SetStreamingState(false)` called on stop so the first new client after `Start()` correctly wins the CAS gate and triggers camera startup.
  - **`OnStreaming` fires only on state change** (`Server.WatchDog`): added `_lastReportedStreamingState` guard — the WatchDog previously fired `OnStreaming(true)` unconditionally every 5 seconds while cameras were running (even with no RTSP clients), flooding consumer apps with spurious `[STREAMING] ACTIVE` events. Now fires only on actual false→true / true→false transitions.
  - **`SetStreamingState` guarded on SETUP** (`PreStartCameraAndEncoder`): wrapped with `if (!_isStreaming)` so the `StreamingStateChanged` event fires once per session instead of once per SETUP request.

  **In-process log subscription:**
  - **`BaluLogger`** (new `BaluLogger.cs`): static wrapper around `Android.Util.Log` that simultaneously writes to logcat and fires `BaluLogger.OnLog` (`EventHandler<BaluLogEventArgs>`). All 264+ `Log.X()` calls across the library have been migrated to `BaluLogger.X()`. Subscribe before creating `Server` to receive startup, encoder-selection, stall, and transport diagnostics in-process without ADB. Handler is wrapped in `try/catch` — subscriber exceptions never propagate to the calling thread.

- v1.5.24: **Text Overlay, Synchronous Streaming, Thread-Safety & Stall Loop Fixes**
  - **Text overlay burn-in** (`Services/FrameOverlay.cs`): up to 4 configurable text slots stamped into NV21/NV12 YUV frames before MediaCodec encodes them. Content types: `DeviceName`, `IpAddress`, `DateTime`, `Date`, `Time`, `Custom`. Anchor positions: all four corners, three top/bottom edge centers, and `Absolute` pixel coordinates. Full RGB color via `OverlayColor` (7 presets + custom). Static slots rendered once; `Time`/`DateTime` refresh each second; `IpAddress` refreshes each minute. Per-frame hot path ≈ 2 µs at 1280×720 (byte-level Y+UV stamp, zero allocation).
  - **Synchronous streaming hot path**: Replaced `await DequeueFrameAsync()` + `await SendBatchAsync()` with synchronous `WaitDequeueFrame()` (OS futex, ~1 ms wake) + `SendBatchSync()` (blocking `socket.Send()`). Eliminates 10–70 ms of async continuation scheduling latency on Android's saturated thread pool. Also removed the 33 ms `FramePacer` sleep that was the dominant latency source once async overhead was fixed. Typical server-side path reduced from ~80 ms to queue ≈ 0.2–0.6 ms · build ≈ 0.2–0.4 ms · send ≈ 1–3 ms.
  - **Thread-safety hardening**: `_isRunning` (H264Encoder), `_isStreaming`/`_isCapturingFront`/`_isCapturingBack` (Server) changed to `volatile`. `Client.IsPlaying` and `Client.ConsecutiveSendErrors` given volatile backing fields — these are written under `lock(client)` in TransportManager but read lock-free in the streaming loop. Fixed missing `lock (_frameFrontLock)` on `_latestFrontFrame = null` in `OnEncoderResolutionFallback`. (Note: `_isStreaming` in `StreamingController` was later upgraded from `volatile bool` to `Interlocked.CompareExchange` in v1.5.25 to close a simultaneous-connect race.)
  - **Idle encoder stall loop fix**: WatchDog now stops H.264 encoders when `ClientCount == 0`, preventing the 200 ms dequeue timeout from looping indefinitely with no clients (triggered by SETUP-without-PLAY health-check connections). Camera capture stays running for fast reconnects. Separately, `activeTimeoutCount` raised from 2 (400 ms) to 25 (5 s) — accommodates cameras that deliver frames at low rates (e.g. VGA on this MediaTek device at ~0.3 fps) without triggering false stall restarts.

- v1.5.23: **Ultra-Low Latency Pipeline Overhaul** — Reduces end-to-end streaming latency from 2-3 seconds to ~100-300ms on LAN.
  - **Buffer depth reduction**: All pipeline channels (camera, encoder input, per-client output) reduced to capacity=1 with `DropOldest`. Eliminates 280-600ms of queue-induced latency — the encoder always processes the freshest frame.
  - **Batch RTP sends**: All RTP packets for an H.264 frame (SPS/PPS + NAL FU-A fragments) are built into a list and sent with a single `socket.SendAsync` call via new `TransportManager.SendBatchAsync()`. Reduces per-frame network overhead from 10-15 syscalls/lock acquisitions to 1.
  - **Encoder loop SpinWait**: Replaced `Thread.Sleep(1)` (1-15ms on Android) with `SpinWait.SpinOnce()` for sub-millisecond encoder responsiveness. Stall detection uses `Stopwatch.GetTimestamp()` instead of `DateTime.UtcNow.Ticks`.
  - **Allocation elimination**: Reusable `CancellationTokenSource` with `TryReset()` for frame dequeue timeouts. Per-send CTS replaced with `socket.SendTimeout = 3000` set at connection time. Activity tracking uses `Environment.TickCount64` instead of `DateTime.UtcNow`. Eliminates ~300+ allocations/sec from the hot path.
  - **Hot-path cleanup**: Removed `Log.Debug` from `FramePacer.RecordDrop()` (JNI + string alloc per dropped frame). Encoder is fed before event subscribers in frame callbacks. Redundant `DateTime.UtcNow` calls removed from H264 streaming loop.

- v1.5.22: **Intermediate Stability & Channel Tuning**
  - **Reduced manager output channel capacity**: `H264EncoderManager` per-client output channel capacity reduced further (from 3 to 2) to keep pipeline depth minimal and latency low.
  - **MediaCodec dequeue timeout reduction**: Input and output buffer dequeue timeouts reduced from 3 ms to 1 ms, cutting worst-case encoder loop sleep time and improving frame throughput on the MediaTek MT6768.
  - **Preliminary SpinWait work in encoder loop**: Introduced initial `SpinWait` usage in `EncodingLoop` to reduce idle wait overhead between frames. (Later replaced by the persistent `SpinWait` instance fix in v1.5.25 to prevent level-0 spin reset on each iteration.)

- v1.5.21: **Long-Running Stability & Latency Fix** — Eliminates overnight SIGABRT crashes and reduces streaming latency.
  - **JNI-free processing thread**: `BackCameraService` and `FrontCameraService` now marshal all `VideoFrame` data into managed `FrameEventArgs` in the `OnFrameAvailable` callback (JNI context) and recycle the native frame immediately. The `ProcessFramesAsync` thread makes zero JNI calls, preventing Mono GC thread-state corruption (`Cannot transition thread from RUNNING with DONE_BLOCKING`).
  - **Channel type change**: Frame channels now hold `FrameEventArgs` (managed) instead of `VideoFrame` (Java object). `DropOldest` is safe again since dropped items have no native resources to leak.
  - **Bitrate fix**: `SetParameters(PARAMETER_KEY_VIDEO_BITRATE)` was incorrectly passing `(int)BitrateMode.CbrFd` (value 3) instead of the actual bitrate (2 Mbps), causing extremely low quality. Now correctly passes `_bitrate`. Also moved `SetParameters` call to after `encoder.Start()` as required by MediaCodec API.
  - **Latency reduction**: Encoder input queue reduced from 5 to 2 frames (80ms vs 200ms). Manager output queue reduced from 10 to 3 frames (120ms vs 400ms). MediaCodec dequeue timeouts reduced from 5ms to 1ms. Total pipeline latency reduced from ~650ms to ~240ms.
  - **Native AAR v2.0.2**: `Image.close()` now called immediately after pixel copy (before callbacks), UV conversion uses bulk copy, queue capacity reduced from 100 to 5.

- v1.5.20: **H.264 Green Corruption Fix** — Fixes green/corrupted image at higher resolutions on MediaTek devices.
  - **NV21 UV plane offset fix**: MediaTek cameras produce oversized buffers (e.g., 1843198 bytes for 1280x720) but the UV plane starts at `width * height` (declared dimensions), not at the end of the buffer. The encoder was reading Y padding data as UV, causing green corruption in the bottom half of the image.
  - **NV12 color format preference**: Changed from `COLOR_FormatYUV420Flexible` to `COLOR_FormatYUV420SemiPlanar` (NV12). Flexible format has undefined buffer layout for raw ByteBuffer writes — its internal plane offsets vary by resolution and vendor. NV12 guarantees Y at offset 0, UV interleaved at `stride * sliceHeight`.
  - **Encoder sliceHeight=0 guard**: Some encoders return 0 for stride/sliceHeight meaning "same as configured". Added guard to prevent `dstYPlaneSize = 0` which would cause UV data to overwrite Y data.
  - **Relaxed sliceHeight deduction**: Accept aligned sliceHeight values within 256 rows of the configured height, accommodating various encoder alignment requirements (16/32/64 boundary).
  - **Resolution change support**: Added `ApplyResolutionChange()` pipeline — stops encoder, clears SPS/PPS, restarts camera at new resolution, disconnects affected clients, and pre-warms encoder.
  - Fixed same UV offset bug in `CropAndDestrideFrame()` fallback path.

- v1.5.19: **Resolution-Change Refinements**
  - **Refined encoder fallback resolution selection**: `GetNearestSupportedResolution()` improved to prefer aspect-ratio-matching resolutions when stepping down from a requested size that the encoder does not support, reducing distortion on non-standard frame sizes.
  - **Improved stride-padding handling**: `WriteFrameToEncoderBuffer` now correctly computes the UV-plane source offset for frames whose buffer stride is not a simple multiple of the width, fixing subtle color banding at non-standard resolutions following a resolution change.

- v1.5.18: **Multi-Resolution Selection Scaffolding**
  - **Added multi-resolution selection via `ServerConfiguration`**: `BackCameraResolution` and `FrontCameraResolution` can now be changed at runtime; a resolution-change request is queued and applied on the next WatchDog cycle.
  - **Initial `ApplyResolutionChange()` pipeline**: Stops the encoder, clears cached SPS/PPS, restarts the camera at the new resolution, disconnects affected clients, and pre-warms the encoder. Note: UV plane color corruption at resolutions above 720p was present in this initial implementation and was fixed in v1.5.20.

- v1.5.17: **VLC Compatibility Release** — Full RFC 2326/4566 compliance for standards-compliant RTSP clients.
  - Case-insensitive RTSP header parsing (`StringComparer.OrdinalIgnoreCase`) — VLC may send headers with varying casing
  - OPTIONS method handled before authentication per RFC 2326 §10.1 — VLC sends unauthenticated OPTIONS as capability probe
  - CRLF (`\r\n`) line endings in all RTSP responses and SDP — `StreamWriter` on Android/Linux defaults to `\n`, but live555 strictly requires `\r\n`
  - `Content-Base` header includes trailing slash for correct relative URL resolution of `trackID=N` per RFC 3986
  - `Range: npt=0.000-` header in PLAY response — required by VLC to confirm playback position
  - Proper `RTP-Info` URL format with base URI + trackID
  - H.264 encoder pre-warming at SETUP time — prevents live555 timeout on first connect by having the encoder ready before PLAY
  - Encoder stall recovery — automatically restarts stalled encoders when subsequent clients connect

- v1.5.16: H.264 Stream Freeze Fix — MediaTek Encoder Quirks, RTP Timestamps, and Client Lifecycle. This release resolves the remaining causes of H.264 stream freezing on MediaTek devices through a combination of encoder configuration fixes, RTP timestamp correction, and client lifecycle hardening.

  - **Encoder Stall from All-IDR Output (PRIMARY ROOT CAUSE)**:

  - Problem: `SetFloat(KeyIFrameInterval, 0.25f)` was misinterpreted by the MediaTek MT6768 as `0`, causing every single frame to become an IDR keyframe. After ~1000 frames, the encoder's internal buffers were exhausted and it stalled permanently — no more output, but input still accepted.

  - Diagnostic: Encoder output logs showed `key=True` on every frame. After frame ~1000, `Frame dequeue timeout (2000ms)` appeared every 2 seconds with no further encoder output.

  - Fix: Changed to `SetInteger(KeyIFrameInterval, 1)`. Always use `SetInteger` (not `SetFloat`) for I-frame interval on Android MediaCodec. Sub-second float values are unreliable on many SoCs. Value of 1 = one IDR keyframe per second (~25 frames at 25fps).

  - **RTP Timestamps 1000x Too Fast (CRITICAL)**:

  - Problem: `EncoderTimestampToRtp` treated MediaCodec's `PresentationTimeUs` as microseconds, but the MT6768 reports values in units approximately 1000x larger than microseconds. This produced RTP timestamp deltas of ~3,000,000 per frame instead of the expected ~3,600 (at 25fps/90kHz clock). Players like VLC interpreted frames as being 33 seconds apart and buffered forever, appearing frozen.

  - Diagnostic: Added NAL diagnostic logging that revealed encoder timestamp deltas of ~33,333,000 between 25fps frames (should be ~40,000 if microseconds).

  - Fix: Replaced encoder-timestamp-based RTP derivation with `Stopwatch` wall-clock time. `BaseEncoderTimestamp` is repurposed to store the `Stopwatch.GetTimestamp()` start tick. RTP offset is calculated as `elapsedSeconds * 90000.0`, which produces correct ~3,600 deltas regardless of encoder timestamp units. This approach is robust across all SoCs.

  - **Client Lifecycle Killing Active Streams**:

  - Problem: Multiple lifecycle mechanisms (WatchDog, HandleClient, RTCP, TransportManager) were prematurely terminating streaming clients due to unreliable `Socket.Connected` checks, single-error disconnection, and disposal race conditions that caused `ObjectDisposedException` in streaming tasks.

  - Fixes:
    - `GetDeadClients()` checks `IsPlaying` first — playing clients are never marked as dead, only non-playing clients are subject to socket checks and grace period timeouts
    - `TransportManager` uses graduated error counting with a threshold of 10 consecutive failures (TCP) or 5 failures / unreachable host (UDP), instead of immediate disconnection on first error
    - `CleanupClient` sets `IsPlaying = false` before calling `Dispose()` to prevent `ObjectDisposedException` in streaming tasks that may still be running on separate threads
    - `HandleClient` uses `ReadLineAsync()` null detection instead of `Socket.Connected` to detect disconnection, avoiding false positives from the unreliable `Connected` property
    - Frame dequeue uses a 2-second timeout to prevent blocking forever on encoder stalls
    - `FramePacer.ShouldDropFrame` fixed: now correctly drops frames arriving too fast (less than half a frame interval), not frames arriving after a gap — the previous inverted logic caused recovery from stalls to be even slower

  - **Encoding Loop Reorder**:

  - Problem: The encoding loop fed input first, then drained output. When the encoder's internal input queue was full (because output hadn't been drained), `FeedInputBuffer` would fail and the frame was lost.

  - Fix: Swapped the order in `EncodingLoop` to drain output before feeding input. This frees encoder resources before attempting to queue new input, reducing unnecessary frame loss on resource-constrained SoCs.

  - **Files Changed**:
    - `RTSP/H264Encoder.cs`: I-frame interval fix (`SetFloat` → `SetInteger`), encoding loop drain-before-feed reorder
    - `RTSP/Transport/RtpPacketBuilder.cs`: Wall-clock `Stopwatch`-based RTP timestamp derivation
    - `RTSP/Transport/TransportManager.cs`: Graduated error counting (10 threshold for TCP, 5 for UDP), SendLock timeout logging
    - `RTSP/Streaming/StreamingController.cs`: 2-second frame dequeue timeout, `ObjectDisposedException` and `ChannelClosedException` handling
    - `RTSP/Streaming/FramePacer.cs`: Inverted drop logic fix (drops fast frames, not slow ones)
    - `RTSP/ClientManagement/ClientManager.cs`: Safe cleanup ordering (`IsPlaying = false` before `Dispose()`), `IsPlaying`-first dead client check
    - `RTSP/Server.cs`: `HandleClient` socket lifecycle fix using `ReadLineAsync` null detection

  - **Debugging Methodology**:

  - This fix was identified through a systematic "debug mode" approach:
    1. Disabled all lifecycle management (WatchDog, RTCP cleanup, HandleClient socket closing) to isolate the actual streaming issue
    2. Added frame counter logging to track frame flow through the entire pipeline (camera → encoder → channel → streaming controller → RTP → transport)
    3. Discovered all-IDR output from encoder logs (`key=True` on every frame)
    4. After I-frame fix, added detailed NAL diagnostic logging (NAL type, size, encoder timestamp, RTP timestamp, SPS/PPS info)
    5. Discovered RTP timestamp delta of ~3,000,000 instead of expected ~3,600
    6. Applied wall-clock timestamp fix — stream became fluid

  - **Performance Metrics**:

  | Metric | Before | After |
  |--------|--------|-------|
  | H.264 Stream Duration | ~30 seconds then freeze | Continuous, unlimited |
  | Encoder Output | Stall after ~1000 frames | Continuous encoding |
  | RTP Timestamp Delta | ~3,000,000 (833x too large) | ~3,600 (correct) |
  | Client Reconnection | Frequent false disconnections | Stable with graduated error tolerance |
  | Frame Recovery After Stall | Slow (drops first frames) | Immediate (drops only bursts) |

  - **Impact**: H.264 RTSP streaming now runs continuously without freezing on MediaTek MT6768 and likely other MediaTek SoCs that share these encoder quirks. The combination of correct I-frame interval configuration, robust RTP timestamp derivation, and hardened client lifecycle management eliminates the three root causes of the freeze. Transient network errors no longer kill the stream, and the encoder no longer stalls from all-IDR output.

- v1.5.15: H.264 Thread Safety Fix and Connection Stability. This release fixes a critical threading bug that caused H.264 streams to freeze after ~2 frames, along with several connection reliability improvements.

  - **H.264 Streaming Freeze Fix (Thread Safety)**:

  - Problem: `FeedFrame()` was calling `FeedInputBuffer()` directly on the camera callback thread while `DrainOutputBuffer()` ran on the encoder thread. These concurrent JNI calls to MediaCodec caused the encoder to stall after ~2 frames.

  - Fix: `FeedFrame()` now routes frames through `_frameChannel` so that both `FeedInputBuffer()` and `DrainOutputBuffer()` are serialized on the encoder thread. This eliminates concurrent JNI access to MediaCodec.

  - **Encoder Channel Capacity**:

  - Fix: Increased `_frameChannel` bounded capacity from 2 to 5 frames, providing better buffering headroom and reducing frame drops during brief processing spikes.

  - **TOCTOU Socket Race Fix**:

  - Problem: The streaming loop called `IsSocketConnected` (using `Socket.Poll`) on the same RTSP socket that `HandleClient` was reading from. This created a time-of-check-to-time-of-use race where the poll would consume data intended for the RTSP reader, causing false disconnection detection.

  - Fix: Removed `IsSocketConnected` from the streaming loop. Connection health is now determined solely by send error counting, which is inherently race-free.

  - **SPS/PPS Deduplication**:

  - Problem: SPS/PPS NAL units were being sent redundantly — both as separate parameter sets before keyframes and embedded within the keyframe data itself.

  - Fix: Added deduplication logic to skip SPS/PPS NAL units when they have already been sent separately before the keyframe, reducing bandwidth waste.

  - **Transport SendLock Timeout**:

  - Problem: The `_sendLock` in `TransportManager` used a CancellationToken-linked timeout. During server lifecycle events (shutdown, restart), the CTS could be cancelled, causing sends to fail silently instead of timing out normally.

  - Fix: Changed to a fixed 3-second timeout (`TimeSpan.FromSeconds(3)`) that is independent of the server CancellationTokenSource.

  - **Standalone Send CTS**:

  - Problem: The send CancellationTokenSource was coupled to the server CTS, meaning server shutdown would immediately cancel in-flight sends without allowing graceful client cleanup.

  - Fix: Decoupled the send timeout CTS from the server CTS, allowing in-progress sends to complete or timeout naturally during shutdown.

  - **Files Changed**:
    - `RTSP/H264Encoder.cs`: Thread safety fix — `FeedFrame()` routes through channel; channel capacity increased to 5
    - `RTSP/Streaming/H264EncoderManager.cs`: SPS/PPS deduplication logic
    - `RTSP/Streaming/StreamingController.cs`: Removed `IsSocketConnected` TOCTOU race; standalone send CTS
    - `RTSP/Transport/TransportManager.cs`: Fixed `_sendLock` timeout to 3 seconds

  - **Impact**: H.264 streaming is now stable and no longer freezes after the first few frames. The thread safety fix resolves the root cause of MediaCodec JNI contention. Connection detection is more reliable without the TOCTOU race, and transport timeouts behave correctly during server lifecycle events.

- v1.5.14: Client Reconnection Bug Fix. This release fixes a critical race condition that caused streams to crash when clients disconnected and reconnected.

  - **Problem**: After a client disconnected and reconnected (or a new client connected), the stream would completely stop:
    - Cameras appeared to crash (flashlight could be enabled, indicating camera release)
    - New clients would block forever waiting for frames
    - The issue occurred due to a semaphore race condition in the frame signaling mechanism

  - **Root Cause Analysis**:
    - The semaphore release logic only released N times where N = current client count
    - When client disconnected, count dropped to 0, so encoder released 0 times
    - New clients connecting between frames would call `WaitAsync()` but never receive a signal
    - This created a deadlock where new clients could never receive frames
    - Additionally, `_streamStarted` flag was never reset, preventing on-demand camera restart

  - **Solution**: Two-part fix for robust client handling:

  - **Semaphore Always Releases At Least Once**:
    ```csharp
    // Before (buggy):
    var clientCount = _clientsBack.Count;  // Could be 0!

    // After (fixed):
    var clientCount = System.Math.Max(1, _clientsBack.Count);  // Always >= 1
    ```
    - Ensures new clients connecting between frames can acquire the semaphore
    - Prevents deadlock when client count temporarily drops to zero
    - `SemaphoreFullException` still prevents overflow

  - **Reset Stream State on Last Client Disconnect**:
    ```csharp
    // In HandleClient finally block:
    if (_clientsBack.Count == 0 && _clientsFront.Count == 0)
    {
        lock (_streamLock)
        {
            if (_clientsBack.Count == 0 && _clientsFront.Count == 0)
            {
                _streamStarted = false;  // Allow on-demand restart
            }
        }
    }
    ```
    - Double-checked locking pattern for thread safety
    - Allows cameras to restart on-demand when new clients connect
    - Logs state change for debugging

  - **Files Changed**:
    - `Services/MjpegServer.cs`:
      - Lines 371, 419: Changed `Math.Max(1, count)` for semaphore release
      - Lines 657-668: Added `_streamStarted` reset in client cleanup

  - **Testing**:
    - Connect MJPEG client, verify streaming works
    - Disconnect client, wait a few seconds
    - Reconnect (same or different device) - stream should resume immediately
    - Verify no "flashlight available" state (cameras stay ready or restart on-demand)

  - **Impact**: Client reconnection now works reliably. The race condition that caused streams to appear "crashed" after disconnect/reconnect cycles is eliminated. This was a critical fix for production deployments where clients may frequently connect and disconnect.

- v1.5.13: MJPEG Streaming Smoothness Improvements. This release significantly improves MJPEG streaming smoothness with architectural improvements inspired by MauiJpegServer.

  - **Problem**: MJPEG streaming could feel choppy or have inconsistent frame delivery:
    - Encoder pushed frames to all clients synchronously
    - No per-client frame rate limiting
    - Clients could starve each other on slow networks
    - No real-time FPS tracking for diagnostics

  - **Solution**: New per-client streaming architecture:

  - **SemaphoreSlim-Based Frame Signaling**:
    - Each client has its own streaming task that waits on a semaphore
    - When encoder produces a frame, it signals all waiting clients simultaneously
    - More efficient than polling-based approaches
    - Clients wake up exactly when frames are available

  - **Per-Client Frame Rate Limiting**:
    - Each client respects a configurable max frame rate (default 30 FPS)
    - Prevents frame bursting that can cause network congestion
    - Smoother, more consistent frame delivery
    - New constructor parameter: `maxFrameRate`

  - **Real-Time FPS Tracking**:
    - Accurate FPS calculation using `Stopwatch`
    - Watchdog logs FPS: `FPS: Back=29.8, Front=30.1`
    - New properties: `BackCameraFps`, `FrontCameraFps`
    - Total frame counters: `TotalBackFrames`, `TotalFrontFrames`

  - **Per-Client Streaming Tasks**:
    - Each client runs its own async streaming loop
    - Clients are independent - slow client doesn't affect others
    - Individual timeout and cleanup per client
    - Better client lifecycle management with `ClientInfo` class

  - **Latest Frame Access**:
    - New methods: `GetLatestBackFrame()`, `GetLatestFrontFrame()`
    - Useful for snapshot endpoints
    - Instant frame access without waiting

  - **API Changes**:
    ```csharp
    // New constructor parameter
    var server = new MjpegServer(
        port: 8089,
        quality: 75,
        maxFrameRate: 30  // NEW: Limit FPS per client
    );

    // New properties
    double backFps = server.BackCameraFps;
    double frontFps = server.FrontCameraFps;
    long totalFrames = server.TotalBackFrames;

    // New methods for snapshots
    byte[]? latestFrame = server.GetLatestBackFrame();
    ```

  - **Files Changed**:
    - `Services/MjpegServer.cs`: Complete rewrite of client streaming architecture

  - **Performance Impact**:
    - Smoother frame delivery with consistent intervals
    - Reduced jitter on variable network conditions
    - Better multi-client performance (clients don't block each other)
    - Lower latency for responsive clients

  - **Impact**: MJPEG streaming is now significantly smoother with consistent frame pacing. The new architecture ensures each client receives frames at a controlled rate, preventing the choppy playback that could occur with the previous push-based approach. Inspired by the clean architecture of MauiJpegServer while retaining BaluMediaServer's advanced features (authentication, HTTPS, etc.).

- v1.5.12: Native Library Frame Delivery Fix - Resolved Stream Stopping Issue. This release fixes a critical bug in the native Android camera library that caused streams to stop completely after running for a short time.

  - **Problem**: MJPEG streams would stop receiving frames after the native library's internal queue filled up:
    - Logs showed: `Back camera queue backing up (81/100), dropping frame`
    - After queue reached 80% capacity, ALL frames were dropped
    - The queue was never consumed by any code (dead/unused feature)
    - Once full, the queue stayed full forever, permanently blocking frame delivery
    - Result: Stream worked initially, then stopped completely with no recovery

  - **Root Cause Analysis**:
    ```kotlin
    // BEFORE (Broken) - in CameraFrameServicev2.kt
    if (queueSize > queueCapacity * 0.8) {  // 80 frames
        Log.w(TAG, "Back camera queue backing up...")
        return  // ← DROPS FRAME ENTIRELY - never sent to callback!
    }
    backCameraCallback?.onFrameAvailable(frame)  // Only reached if queue < 80%
    backCameraFrameQueue.offer(frame)  // Queue never consumed!
    ```
    The frame dropping check was placed BEFORE sending to callbacks. When the unused queue filled up, it blocked the primary frame delivery path.

  - **Solution**: Restructured frame delivery to prioritize callbacks:
    ```kotlin
    // AFTER (Fixed)
    // Send to callback FIRST - this is the primary consumer (MJPEG streaming)
    backCameraCallback?.onFrameAvailable(frame)

    // Queue is optional secondary storage - only add if there's room
    if (!backCameraFrameQueue.offer(frame)) {
        // Queue full - drop oldest, but callback already received the frame
        val droppedFrame = backCameraFrameQueue.poll()
        droppedFrame?.let { backBufferPool.release(it.data) }
        backCameraFrameQueue.offer(frame)
    }
    ```

  - **Files Changed**:
    - `AndroidLib/camerastreamer/src/main/java/CameraFrameServicev2.kt`: Fixed frame delivery for both front and back cameras
    - Native library version bumped to v2.0.1

  - **How to Update**:
    1. Rebuild the AndroidLib: `./gradlew :camerastreamer:assembleRelease`
    2. Copy `camerastreamer-release.aar` to `BaluMediaServer/Jar/`
    3. Rebuild your application

  - **Impact**: This was the actual root cause of stream stopping issues. The fix ensures frames are always delivered to .NET regardless of internal queue state. Streams now run continuously without any frame drops or interruptions. The internal queue remains available for future use cases but no longer blocks primary frame delivery.

- v1.5.11: Continuous Streaming Mode - Eliminated Stream Interruptions. This release disables all automatic stop mechanisms to ensure fluid, uninterrupted streaming without cuts or frame drops.

  - **Problem**: The WatchDog and auto-stop mechanisms were causing stream interruptions:
    - Cameras stopped when last client disconnected
    - Encoders stopped and restarted frequently
    - Camera restart commands triggered by MJPEG watchdog
    - Stream cuts and frame drops during client transitions
    - Reconnection delays due to encoder restarts

  - **Solution**: Disabled all automatic stop mechanisms for continuous operation:

  - **Disabled WatchDog Auto-Stop**:
    - WatchDog now only cleans up dead/disconnected clients (preserves important cleanup)
    - Cameras keep running regardless of client count
    - Encoders keep running regardless of client count
    - Streaming state persists once started
    - No more automatic encoder/camera stops

  - **Disabled EventBus Camera Stop Commands**:
    - `STOP_CAMERA_FRONT` and `STOP_CAMERA_BACK` commands logged but ignored
    - Prevents external code from interrupting streams
    - Cameras stay active for instant client connections

  - **Disabled MJPEG Watchdog Restarts**:
    - Watchdog logs frame delays but doesn't restart cameras
    - No more camera restarts after temporary delays
    - Eliminates stream cuts from camera restarts

  - **Benefits**:
    - ✅ **Zero Interruptions**: Streams never cut when clients disconnect/reconnect
    - ✅ **Instant Reconnection**: No encoder restart delay (was ~1-2 seconds)
    - ✅ **Fluid Experience**: No frame drops during client transitions
    - ✅ **Production Ready**: Reliable, predictable behavior
    - ✅ **Better Multi-Client**: New clients can connect instantly without affecting others

  - **Trade-offs**:
    - ⚠️ **Continuous Resource Usage**: Cameras and encoders run even with no clients
    - ⚠️ **Battery Drain**: Continuous operation uses more power on mobile devices
    - ⚠️ **Manual Control**: Must explicitly call `Stop()` or `Dispose()` to stop streaming

  - **How to Stop**:
    - Call `server.Stop()` to stop everything
    - Call `server.Dispose()` to release all resources
    - Application exit automatically stops everything

  - **Monitoring**:
    - WatchDog now logs status: `Active clients: RTSP=0, MJPEG=2, Cameras: Back=True, Front=False, Streaming=True`
    - Helps monitor server state without auto-stop interference

  - **Configuration**:
    - Currently hardcoded for maximum reliability
    - See `CONTINUOUS_STREAMING_MODE.md` for instructions to restore auto-stop if needed
    - Future: Configuration flag for hybrid mode (auto-stop on battery, continuous on power)

  - **Files Changed**:
    - `RTSP/Server.cs`: Disabled WatchDog auto-stop and EventBus camera stop commands
    - `Services/MjpegServer.cs`: Disabled watchdog camera restarts
    - `CONTINUOUS_STREAMING_MODE.md`: Complete documentation

  - **Performance Impact**:
    - CPU: Minimal - encoders efficient, H.264 only encodes when frames available
    - Memory: Minimal - bounded buffers with DropOldest prevent buildup
    - Battery: Moderate increase on mobile (camera always on)
    - Network: Zero impact when no clients (no data sent)

  - **Impact**: Streams are now completely fluid with zero interruptions. Perfect for scenarios where reliability is critical (security cameras, monitoring systems, live broadcasts). The server maintains ready state for instant client connections. Trade-off of continuous resource usage is acceptable for server/desktop deployments and provides significantly better user experience.

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

- v1.5.2: Comprehensive Code Documentation. Added XML documentation comments to all public classes, methods, properties, and events.

  - **Models**: `FrameEventArgs`, `H264FrameEventArgs`, `Client`, `VideoProfile`, `ServerConfiguration`, `RtspRequest`, `RtspAuth`, `EncoderInfo`, `AuthType`, `CodecType`, `BussCommand`, `TransportMode`

  - **Services**: `Server`, `MjpegServer`, `FrontCameraService`, `BackCameraService`

  - **Encoders**: `H264Encoder` (general-purpose), `MediaTekH264Encoder` (MediaTek-optimized)

  - **Utilities**: `EventBuss`, `FrameConverterHelper`, `FrameCallback`, `ICameraService`

  - Benefits:
    - Full IntelliSense support in Visual Studio and VS Code
    - Auto-generated API documentation capability
    - Improved code maintainability and developer experience

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

- v1.2.0: Adding new global encoder for compatiblity with multiple devices not only Mediatek and fixing some features from the server to handle clients.

- v1.1.11: Fixing Server to allow Configuration Class, fixing MjpegServer disposal on Server class, fixing MjpegServer to set a fixed bitrate to 30 fps and fixing CPU leaks.

- v1.1.10: Adding a custom class 'ServerConfiguration' to handle more easily all the server configurations.

- v1.1.9: Adding user/password handling options.

- v1.1.8: Fixing issues related with Camera Services, making that on camera or service closure do not allow to restart them.

- v1.1.7: Fixing MJPEG Codec bugs avoiding crashes, fixing Watchdog that close prematurly some connections, fixing some issues with the preview.

- v1.1.6: Adding ArrayPool to avoid ovearhead at GC with multiple byte[] creations like in RTP Packets.
Adding .ConfigureAwait(false) on awaitable method to avoid context overhead, theorical from 100ms to 100 us, increase performance on fewer CPU resources devices.

- v1.1.5: Fixing EventBuss command on Server class, if the server was started do not raise the flag into it, and sometimes make the app crash due to "Port already in use" or even using excesive CPU on multiple MJPEG servers.
Adding to MJPEGServer preview of EventBuss to handle it by there, but needs sync with main server to avoid duplicate instances or commands.

- v1.1.4: Adding auth option into CTOR of Server class, to enable or disable auth on stream rtsp, adding feature to determina video quality into mjpeg server

- v1.1.3: Adding handling for auto-quality adjust based on rtcp control for MJPEG codec, allowing to increase or decrease the image quality to guarantee video stability over this codec.
-- Adding a preview (WIP) for video profiles allowing to create custom paths for this new profiles, will allow to set a custom resolution, bitrate and more.

- v1.1.2: Adding at Server CTOR two new variables to handle if the front or back camera should be enabled, this avoid the problem that only one camera start on devices that can not handle both cameras at same time.

---

**Thanks for checking out Balu Media Server!** 

Feel free to report bugs, suggest features, or fork and play around with the code.

**Let's make mobile RTSP with C# a thing!** 💪

---

*Made with ❤️ and C# • Open Source • MIT Licensed*