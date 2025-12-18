using BaluMediaServer.Models;
using FluentAssertions;
using Xunit;

namespace BaluMediaServer.Tests.Unit.Models;

/// <summary>
/// Unit tests for the <see cref="RtspRequest"/> class.
/// Tests CSeq parsing and header handling.
/// </summary>
public class RtspRequestTests
{
    #region Default Values Tests

    [Fact]
    public void Constructor_ShouldSetDefaultMethod_ToEmptyString()
    {
        // Arrange & Act
        var request = new RtspRequest();

        // Assert
        request.Method.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultUri_ToEmptyString()
    {
        // Arrange & Act
        var request = new RtspRequest();

        // Assert
        request.Uri.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultVersion_ToEmptyString()
    {
        // Arrange & Act
        var request = new RtspRequest();

        // Assert
        request.Version.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultBody_ToEmptyString()
    {
        // Arrange & Act
        var request = new RtspRequest();

        // Assert
        request.Body.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ShouldInitializeHeaders_AsEmptyDictionary()
    {
        // Arrange & Act
        var request = new RtspRequest();

        // Assert
        request.Headers.Should().NotBeNull();
        request.Headers.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultAuth_ToNull()
    {
        // Arrange & Act
        var request = new RtspRequest();

        // Assert
        request.Auth.Should().BeNull();
    }

    #endregion

    #region CSeq Parsing Tests

    [Fact]
    public void CSeq_WhenHeaderNotPresent_ShouldReturnZero()
    {
        // Arrange
        var request = new RtspRequest();

        // Act
        var cseq = request.CSeq;

        // Assert
        cseq.Should().Be(0);
    }

    [Fact]
    public void CSeq_WhenHeaderPresent_ShouldReturnParsedValue()
    {
        // Arrange
        var request = new RtspRequest();
        request.Headers["CSeq"] = "5";

        // Act
        var cseq = request.CSeq;

        // Assert
        cseq.Should().Be(5);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("100", 100)]
    [InlineData("999", 999)]
    public void CSeq_ShouldParse_VariousValidValues(string headerValue, int expectedCSeq)
    {
        // Arrange
        var request = new RtspRequest();
        request.Headers["CSeq"] = headerValue;

        // Act
        var cseq = request.CSeq;

        // Assert
        cseq.Should().Be(expectedCSeq);
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void Method_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var request = new RtspRequest();

        // Act
        request.Method = "DESCRIBE";

        // Assert
        request.Method.Should().Be("DESCRIBE");
    }

    [Theory]
    [InlineData("OPTIONS")]
    [InlineData("DESCRIBE")]
    [InlineData("SETUP")]
    [InlineData("PLAY")]
    [InlineData("PAUSE")]
    [InlineData("TEARDOWN")]
    public void Method_ShouldAccept_AllRtspMethods(string method)
    {
        // Arrange
        var request = new RtspRequest();

        // Act
        request.Method = method;

        // Assert
        request.Method.Should().Be(method);
    }

    [Fact]
    public void Uri_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var request = new RtspRequest();

        // Act
        request.Uri = "rtsp://192.168.1.1:7778/live/back";

        // Assert
        request.Uri.Should().Be("rtsp://192.168.1.1:7778/live/back");
    }

    [Fact]
    public void Version_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var request = new RtspRequest();

        // Act
        request.Version = "RTSP/1.0";

        // Assert
        request.Version.Should().Be("RTSP/1.0");
    }

    [Fact]
    public void Body_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var request = new RtspRequest();

        // Act
        request.Body = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\n";

        // Assert
        request.Body.Should().Be("v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\n");
    }

    #endregion

    #region Headers Tests

    [Fact]
    public void Headers_ShouldAllowAdding_MultipleEntries()
    {
        // Arrange
        var request = new RtspRequest();

        // Act
        request.Headers["CSeq"] = "1";
        request.Headers["User-Agent"] = "TestClient";
        request.Headers["Accept"] = "application/sdp";

        // Assert
        request.Headers.Should().HaveCount(3);
        request.Headers["CSeq"].Should().Be("1");
        request.Headers["User-Agent"].Should().Be("TestClient");
        request.Headers["Accept"].Should().Be("application/sdp");
    }

    [Fact]
    public void Headers_ShouldAllowUpdating_ExistingEntries()
    {
        // Arrange
        var request = new RtspRequest();
        request.Headers["CSeq"] = "1";

        // Act
        request.Headers["CSeq"] = "2";

        // Assert
        request.Headers["CSeq"].Should().Be("2");
    }

    #endregion

    #region Auth Tests

    [Fact]
    public void Auth_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var request = new RtspRequest();
        var auth = new RtspAuth
        {
            Username = "admin",
            Password = "password123",
            Type = AuthType.Basic
        };

        // Act
        request.Auth = auth;

        // Assert
        request.Auth.Should().NotBeNull();
        request.Auth!.Username.Should().Be("admin");
        request.Auth.Password.Should().Be("password123");
        request.Auth.Type.Should().Be(AuthType.Basic);
    }

    #endregion
}
