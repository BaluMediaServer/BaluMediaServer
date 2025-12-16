namespace BaluMediaServer.Models;

/// <summary>
/// Defines commands for the event bus system to control camera and server services.
/// Used for decoupled communication between components.
/// </summary>
public enum BussCommand : int
{
    /// <summary>
    /// Command to start the front camera capture service.
    /// </summary>
    START_CAMERA_FRONT,

    /// <summary>
    /// Command to stop the front camera capture service.
    /// </summary>
    STOP_CAMERA_FRONT,

    /// <summary>
    /// Command to start the back camera capture service.
    /// </summary>
    START_CAMERA_BACK,

    /// <summary>
    /// Command to stop the back camera capture service.
    /// </summary>
    STOP_CAMERA_BACK,

    /// <summary>
    /// Command to start the MJPEG HTTP server.
    /// </summary>
    START_MJPEG_SERVER,

    /// <summary>
    /// Command to stop the MJPEG HTTP server.
    /// </summary>
    STOP_MJPEG_SERVER,

    /// <summary>
    /// Command to switch between front and back cameras.
    /// Reserved for future implementation.
    /// </summary>
    SWITCH_CAMERA,
}
