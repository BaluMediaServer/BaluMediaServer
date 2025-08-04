namespace BaluMediaServer.Models;

public enum BussCommand : int
{
    START_CAMERA_FRONT,
    STOP_CAMERA_FRONT,
    START_CAMERA_BACK,
    STOP_CAMERA_BACK,
    START_MJPEG_SERVER,
    STOP_MJPEG_SERVER,
    SWITCH_CAMERA,
}