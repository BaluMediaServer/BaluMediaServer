using BaluMediaServer.Models;
using FluentAssertions;
using Xunit;

namespace BaluMediaServer.Tests.Unit.Models;

/// <summary>
/// Unit tests for the <see cref="ServerConfiguration"/> class.
/// Tests default values and property initialization.
/// </summary>
public class ServerConfigurationTests
{
    #region Default Values Tests

    [Fact]
    public void Constructor_ShouldSetDefaultPort_To7778()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.Port.Should().Be(7778);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultMaxClients_To10()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.MaxClients.Should().Be(10);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultMjpegServerQuality_To80()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.MjpegServerQuality.Should().Be(80);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultMjpegServerPort_To8089()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.MjpegServerPort.Should().Be(8089);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultAuthRequired_ToTrue()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.AuthRequired.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultFrontCameraEnabled_ToTrue()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.FrontCameraEnabled.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultBackCameraEnabled_ToTrue()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.BackCameraEnabled.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultStartMjpegServer_ToTrue()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.StartMjpegServer.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultBaseAddress_To0000()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.BaseAddress.Should().Be("0.0.0.0");
    }

    [Fact]
    public void Constructor_ShouldSetDefaultUseHttps_ToFalse()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.UseHttps.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultCertificatePath_ToNull()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.CertificatePath.Should().BeNull();
    }

    [Fact]
    public void Constructor_ShouldSetDefaultCertificatePassword_ToNull()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.CertificatePassword.Should().BeNull();
    }

    #endregion

    #region Collection Initialization Tests

    [Fact]
    public void Users_ShouldInitialize_AsEmptyDictionary()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.Users.Should().NotBeNull();
        config.Users.Should().BeEmpty();
    }

    [Fact]
    public void Users_ShouldAllowAdding_NewEntries()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.Users.Add("admin", "password123");

        // Assert
        config.Users.Should().ContainKey("admin");
        config.Users["admin"].Should().Be("password123");
    }

    #endregion

    #region Video Profile Tests

    [Fact]
    public void PrimaryProfile_ShouldInitialize_AsNewVideoProfile()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.PrimaryProfile.Should().NotBeNull();
        config.PrimaryProfile.Should().BeOfType<VideoProfile>();
    }

    [Fact]
    public void SecondaryProfile_ShouldInitialize_AsNewVideoProfile()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.SecondaryProfile.Should().NotBeNull();
        config.SecondaryProfile.Should().BeOfType<VideoProfile>();
    }

    [Fact]
    public void PrimaryProfile_ShouldHaveDefaultValues()
    {
        // Arrange & Act
        var config = new ServerConfiguration();

        // Assert
        config.PrimaryProfile.Height.Should().Be(640);
        config.PrimaryProfile.Width.Should().Be(480);
        config.PrimaryProfile.Quality.Should().Be(80);
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void Port_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.Port = 9999;

        // Assert
        config.Port.Should().Be(9999);
    }

    [Fact]
    public void MaxClients_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.MaxClients = 50;

        // Assert
        config.MaxClients.Should().Be(50);
    }

    [Fact]
    public void BaseAddress_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.BaseAddress = "192.168.1.100";

        // Assert
        config.BaseAddress.Should().Be("192.168.1.100");
    }

    [Fact]
    public void UseHttps_WhenSetToTrue_ShouldReturnTrue()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.UseHttps = true;

        // Assert
        config.UseHttps.Should().BeTrue();
    }

    [Fact]
    public void CertificatePath_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.CertificatePath = "/path/to/certificate.pfx";

        // Assert
        config.CertificatePath.Should().Be("/path/to/certificate.pfx");
    }

    [Fact]
    public void CertificatePassword_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.CertificatePassword = "securepassword";

        // Assert
        config.CertificatePassword.Should().Be("securepassword");
    }

    #endregion

    #region Camera Configuration Tests

    [Fact]
    public void FrontCameraEnabled_WhenSetToFalse_ShouldReturnFalse()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.FrontCameraEnabled = false;

        // Assert
        config.FrontCameraEnabled.Should().BeFalse();
    }

    [Fact]
    public void BackCameraEnabled_WhenSetToFalse_ShouldReturnFalse()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.BackCameraEnabled = false;

        // Assert
        config.BackCameraEnabled.Should().BeFalse();
    }

    [Fact]
    public void AuthRequired_WhenSetToFalse_ShouldReturnFalse()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.AuthRequired = false;

        // Assert
        config.AuthRequired.Should().BeFalse();
    }

    #endregion

    #region MJPEG Server Configuration Tests

    [Fact]
    public void MjpegServerQuality_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.MjpegServerQuality = 50;

        // Assert
        config.MjpegServerQuality.Should().Be(50);
    }

    [Fact]
    public void MjpegServerPort_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.MjpegServerPort = 8080;

        // Assert
        config.MjpegServerPort.Should().Be(8080);
    }

    [Fact]
    public void StartMjpegServer_WhenSetToFalse_ShouldReturnFalse()
    {
        // Arrange
        var config = new ServerConfiguration();

        // Act
        config.StartMjpegServer = false;

        // Assert
        config.StartMjpegServer.Should().BeFalse();
    }

    #endregion
}
