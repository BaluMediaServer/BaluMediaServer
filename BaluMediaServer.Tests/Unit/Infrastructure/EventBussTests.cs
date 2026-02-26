using BaluMediaServer.Models;
using BaluMediaServer.Repositories;
using FluentAssertions;
using Xunit;

namespace BaluMediaServer.Tests.Unit.Infrastructure;

/// <summary>
/// Unit tests for the <see cref="EventBuss"/> class.
/// Tests command propagation and event subscription patterns.
/// </summary>
public class EventBussTests : IDisposable
{
    private BussCommand? _receivedCommand;
    private int _invocationCount;
    private readonly Action<BussCommand> _handler;

    public EventBussTests()
    {
        _receivedCommand = null;
        _invocationCount = 0;
        _handler = (cmd) =>
        {
            _receivedCommand = cmd;
            _invocationCount++;
        };
    }

    public void Dispose()
    {
        // Unsubscribe to clean up between tests
        EventBuss.Command -= _handler;
    }

    #region SendCommand Tests

    [Fact]
    public void SendCommand_WhenSubscribed_ShouldInvokeHandler()
    {
        // Arrange
        EventBuss.Command += _handler;

        // Act
        EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);

        // Assert
        _receivedCommand.Should().Be(BussCommand.START_CAMERA_BACK);
        _invocationCount.Should().Be(1);
    }

    [Fact]
    public void SendCommand_WhenNoSubscribers_ShouldNotThrow()
    {
        // Arrange - No subscription

        // Act
        var action = () => EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);

        // Assert
        action.Should().NotThrow();
    }

    [Theory]
    [InlineData(BussCommand.START_CAMERA_FRONT)]
    [InlineData(BussCommand.STOP_CAMERA_FRONT)]
    [InlineData(BussCommand.START_CAMERA_BACK)]
    [InlineData(BussCommand.STOP_CAMERA_BACK)]
    [InlineData(BussCommand.START_MJPEG_SERVER)]
    [InlineData(BussCommand.STOP_MJPEG_SERVER)]
    [InlineData(BussCommand.SWITCH_CAMERA)]
    public void SendCommand_ShouldPropagate_AllCommandTypes(BussCommand command)
    {
        // Arrange
        EventBuss.Command += _handler;

        // Act
        EventBuss.SendCommand(command);

        // Assert
        _receivedCommand.Should().Be(command);
    }

    #endregion

    #region Multiple Subscribers Tests

    [Fact]
    public void SendCommand_WithMultipleSubscribers_ShouldInvokeAll()
    {
        // Arrange
        var receivedCommands = new List<BussCommand>();
        Action<BussCommand> handler1 = (cmd) => receivedCommands.Add(cmd);
        Action<BussCommand> handler2 = (cmd) => receivedCommands.Add(cmd);
        Action<BussCommand> handler3 = (cmd) => receivedCommands.Add(cmd);

        EventBuss.Command += handler1;
        EventBuss.Command += handler2;
        EventBuss.Command += handler3;

        try
        {
            // Act
            EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);

            // Assert
            receivedCommands.Should().HaveCount(3);
            receivedCommands.Should().AllSatisfy(cmd => cmd.Should().Be(BussCommand.START_CAMERA_BACK));
        }
        finally
        {
            // Cleanup
            EventBuss.Command -= handler1;
            EventBuss.Command -= handler2;
            EventBuss.Command -= handler3;
        }
    }

    #endregion

    #region Subscription Management Tests

    [Fact]
    public void Unsubscribe_ShouldPreventFutureInvocations()
    {
        // Arrange
        EventBuss.Command += _handler;
        EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
        _invocationCount.Should().Be(1);

        // Act - Unsubscribe
        EventBuss.Command -= _handler;
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_BACK);

        // Assert - Count should still be 1
        _invocationCount.Should().Be(1);
    }

    [Fact]
    public void SendCommand_MultipleTimes_ShouldInvokeHandlerEachTime()
    {
        // Arrange
        EventBuss.Command += _handler;

        // Act
        EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_BACK);
        EventBuss.SendCommand(BussCommand.START_MJPEG_SERVER);

        // Assert
        _invocationCount.Should().Be(3);
        _receivedCommand.Should().Be(BussCommand.START_MJPEG_SERVER); // Last command
    }

    #endregion
}
