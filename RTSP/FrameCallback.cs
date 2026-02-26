using Kotlin.Jvm.Functions;
using Java.Lang;
using Android.Runtime;

/// <summary>
/// Callback wrapper for receiving frame data from Kotlin/Java code.
/// Implements the Kotlin Function1 interface to bridge between Java and C#.
/// </summary>
public class FrameCallback : Java.Lang.Object, IFunction1
{
    private readonly Action<byte[]> _callback;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrameCallback"/> class.
    /// </summary>
    /// <param name="callback">The C# action to invoke when a frame is received.</param>
    public FrameCallback(Action<byte[]> callback)
    {
        _callback = callback;
    }

    /// <summary>
    /// Invoked by Kotlin code when a new frame is available.
    /// Converts the Java byte array to a C# byte array and invokes the callback.
    /// </summary>
    /// <param name="p0">The Java object containing the frame data (expected to be a byte array).</param>
    /// <returns>Always returns null as this is a void operation.</returns>
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
