using BaluMediaServer.Models;
using FluentAssertions;
using Xunit;

namespace BaluMediaServer.Tests.Unit.Enums;

/// <summary>
/// Unit tests for enum types in the BaluMediaServer library.
/// Validates enum values and ensures API stability.
/// </summary>
public class EnumTests
{
    #region AuthType Tests

    [Fact]
    public void AuthType_ShouldContain_NoneValue()
    {
        // Assert
        Enum.IsDefined(typeof(AuthType), AuthType.None).Should().BeTrue();
    }

    [Fact]
    public void AuthType_ShouldContain_BasicValue()
    {
        // Assert
        Enum.IsDefined(typeof(AuthType), AuthType.Basic).Should().BeTrue();
    }

    [Fact]
    public void AuthType_ShouldContain_DigestValue()
    {
        // Assert
        Enum.IsDefined(typeof(AuthType), AuthType.Digest).Should().BeTrue();
    }

    [Fact]
    public void AuthType_ShouldHave_ExactlyThreeValues()
    {
        // Arrange
        var values = Enum.GetValues(typeof(AuthType));

        // Assert
        values.Length.Should().Be(3);
    }

    [Theory]
    [InlineData(AuthType.None, 0)]
    [InlineData(AuthType.Basic, 1)]
    [InlineData(AuthType.Digest, 2)]
    public void AuthType_ShouldHave_ExpectedIntValues(AuthType authType, int expectedValue)
    {
        // Assert
        ((int)authType).Should().Be(expectedValue);
    }

    #endregion

    #region CodecType Tests

    [Fact]
    public void CodecType_ShouldContain_MJPEGValue()
    {
        // Assert
        Enum.IsDefined(typeof(CodecType), CodecType.MJPEG).Should().BeTrue();
    }

    [Fact]
    public void CodecType_ShouldContain_H264Value()
    {
        // Assert
        Enum.IsDefined(typeof(CodecType), CodecType.H264).Should().BeTrue();
    }

    [Fact]
    public void CodecType_ShouldHave_ExactlyTwoValues()
    {
        // Arrange
        var values = Enum.GetValues(typeof(CodecType));

        // Assert
        values.Length.Should().Be(2);
    }

    [Theory]
    [InlineData(CodecType.MJPEG, 0)]
    [InlineData(CodecType.H264, 1)]
    public void CodecType_ShouldHave_ExpectedIntValues(CodecType codecType, int expectedValue)
    {
        // Assert
        ((int)codecType).Should().Be(expectedValue);
    }

    #endregion

    #region BussCommand Tests

    [Fact]
    public void BussCommand_ShouldContain_StartCameraFront()
    {
        // Assert
        Enum.IsDefined(typeof(BussCommand), BussCommand.START_CAMERA_FRONT).Should().BeTrue();
    }

    [Fact]
    public void BussCommand_ShouldContain_StopCameraFront()
    {
        // Assert
        Enum.IsDefined(typeof(BussCommand), BussCommand.STOP_CAMERA_FRONT).Should().BeTrue();
    }

    [Fact]
    public void BussCommand_ShouldContain_StartCameraBack()
    {
        // Assert
        Enum.IsDefined(typeof(BussCommand), BussCommand.START_CAMERA_BACK).Should().BeTrue();
    }

    [Fact]
    public void BussCommand_ShouldContain_StopCameraBack()
    {
        // Assert
        Enum.IsDefined(typeof(BussCommand), BussCommand.STOP_CAMERA_BACK).Should().BeTrue();
    }

    [Fact]
    public void BussCommand_ShouldContain_StartMjpegServer()
    {
        // Assert
        Enum.IsDefined(typeof(BussCommand), BussCommand.START_MJPEG_SERVER).Should().BeTrue();
    }

    [Fact]
    public void BussCommand_ShouldContain_StopMjpegServer()
    {
        // Assert
        Enum.IsDefined(typeof(BussCommand), BussCommand.STOP_MJPEG_SERVER).Should().BeTrue();
    }

    [Fact]
    public void BussCommand_ShouldContain_SwitchCamera()
    {
        // Assert
        Enum.IsDefined(typeof(BussCommand), BussCommand.SWITCH_CAMERA).Should().BeTrue();
    }

    [Fact]
    public void BussCommand_ShouldHave_ExactlySevenValues()
    {
        // Arrange
        var values = Enum.GetValues(typeof(BussCommand));

        // Assert
        values.Length.Should().Be(7);
    }

    [Theory]
    [InlineData(BussCommand.START_CAMERA_FRONT, 0)]
    [InlineData(BussCommand.STOP_CAMERA_FRONT, 1)]
    [InlineData(BussCommand.START_CAMERA_BACK, 2)]
    [InlineData(BussCommand.STOP_CAMERA_BACK, 3)]
    [InlineData(BussCommand.START_MJPEG_SERVER, 4)]
    [InlineData(BussCommand.STOP_MJPEG_SERVER, 5)]
    [InlineData(BussCommand.SWITCH_CAMERA, 6)]
    public void BussCommand_ShouldHave_ExpectedIntValues(BussCommand command, int expectedValue)
    {
        // Assert
        ((int)command).Should().Be(expectedValue);
    }

    #endregion

    #region Enum Parsing Tests

    [Theory]
    [InlineData("None", AuthType.None)]
    [InlineData("Basic", AuthType.Basic)]
    [InlineData("Digest", AuthType.Digest)]
    public void AuthType_ShouldParse_FromString(string value, AuthType expected)
    {
        // Act
        var parsed = Enum.Parse<AuthType>(value);

        // Assert
        parsed.Should().Be(expected);
    }

    [Theory]
    [InlineData("MJPEG", CodecType.MJPEG)]
    [InlineData("H264", CodecType.H264)]
    public void CodecType_ShouldParse_FromString(string value, CodecType expected)
    {
        // Act
        var parsed = Enum.Parse<CodecType>(value);

        // Assert
        parsed.Should().Be(expected);
    }

    [Theory]
    [InlineData("START_CAMERA_FRONT", BussCommand.START_CAMERA_FRONT)]
    [InlineData("STOP_CAMERA_FRONT", BussCommand.STOP_CAMERA_FRONT)]
    [InlineData("START_CAMERA_BACK", BussCommand.START_CAMERA_BACK)]
    [InlineData("STOP_CAMERA_BACK", BussCommand.STOP_CAMERA_BACK)]
    public void BussCommand_ShouldParse_FromString(string value, BussCommand expected)
    {
        // Act
        var parsed = Enum.Parse<BussCommand>(value);

        // Assert
        parsed.Should().Be(expected);
    }

    #endregion

    #region Enum ToString Tests

    [Theory]
    [InlineData(AuthType.None, "None")]
    [InlineData(AuthType.Basic, "Basic")]
    [InlineData(AuthType.Digest, "Digest")]
    public void AuthType_ToString_ShouldReturn_ExpectedString(AuthType authType, string expected)
    {
        // Assert
        authType.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(CodecType.MJPEG, "MJPEG")]
    [InlineData(CodecType.H264, "H264")]
    public void CodecType_ToString_ShouldReturn_ExpectedString(CodecType codecType, string expected)
    {
        // Assert
        codecType.ToString().Should().Be(expected);
    }

    #endregion
}
