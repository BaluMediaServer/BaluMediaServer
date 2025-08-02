using BaluMediaServer.Models;

namespace BaluMediaServer.Repositories;

public class EventBuss
{
    public static event Action<BussCommand>? Command;
    public static void SendCommand(BussCommand command)
    {
        Command?.Invoke(command);
    }
}