using Com.BaluMedia.CameraStreamer;

namespace BaluMediaServer.Interfaces;

public interface ICameraService : IDisposable
{
    public void StartCapture(int width, int height);
    public void StopCapture();
    public void ProcessFrame(VideoFrame frame);
    public void ProcessFrames();
}