using BaluMediaServer.Models;

namespace BaluMediaServer.Repositories;

/// <summary>
/// Event bus for decoupled communication between components.
/// Allows sending commands to control camera and server services without direct dependencies.
/// </summary>
public class EventBuss
{
    /// <summary>
    /// Event raised when a command is sent through the event bus.
    /// Subscribe to this event to receive and handle commands.
    /// </summary>
    public static event Action<BussCommand>? Command;

    /// <summary>
    /// Sends a command through the event bus to all subscribers.
    /// </summary>
    /// <param name="command">The command to send.</param>
    public static void SendCommand(BussCommand command)
    {
        Command?.Invoke(command);
    }
}
