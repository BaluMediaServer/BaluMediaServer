using Kotlin.Jvm.Functions;
using Java.Lang;
using Android.Runtime;

public class FrameCallback : Java.Lang.Object, IFunction1
{
    private readonly Action<byte[]> _callback;

    public FrameCallback(Action<byte[]> callback)
    {
        _callback = callback;
    }

    public Java.Lang.Object Invoke(Java.Lang.Object? p0)
    {
        if (p0 is JavaArray<byte> javaArray)
        {
            var bytes = new byte[javaArray.Count];
            for (int i = 0; i < javaArray.Count; i++)
                bytes[i] = javaArray.ElementAt(i);
            _callback?.Invoke(bytes);
        }
        else
        {
            // Unexpected type, optionally handle or ignore
        }

        return null!;
    }
}
