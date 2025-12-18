using BaluMediaServer.Models;
using FluentAssertions;
using Xunit;

namespace BaluMediaServer.Tests.Unit.Models;

/// <summary>
/// Unit tests for the <see cref="VideoProfile"/> class.
/// Tests quality clamping, name sanitization, and default values.
/// </summary>
public class VideoProfileTests
{
    #region Default Values Tests

    [Fact]
    public void Constructor_ShouldSetDefaultHeight_To640()
    {
        // Arrange & Act
        var profile = new VideoProfile();

        // Assert
        profile.Height.Should().Be(640);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultWidth_To480()
    {
        // Arrange & Act
        var profile = new VideoProfile();

        // Assert
        profile.Width.Should().Be(480);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultMaxBitrate_To4000000()
    {
        // Arrange & Act
        var profile = new VideoProfile();

        // Assert
        profile.MaxBitrate.Should().Be(4000000);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultMinBitrate_To500000()
    {
        // Arrange & Act
        var profile = new VideoProfile();

        // Assert
        profile.MinBitrate.Should().Be(500000);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultQuality_To80()
    {
        // Arrange & Act
        var profile = new VideoProfile();

        // Assert
        profile.Quality.Should().Be(80);
    }

    [Fact]
    public void Constructor_ShouldSetDefaultName_ToEmptyString()
    {
        // Arrange & Act
        var profile = new VideoProfile();

        // Assert
        profile.Name.Should().BeEmpty();
    }

    #endregion

    #region Quality Clamping Tests

    [Fact]
    public void Quality_WhenSetAbove100_ShouldClampTo100()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Quality = 150;

        // Assert
        profile.Quality.Should().Be(100);
    }

    [Fact]
    public void Quality_WhenSetTo100_ShouldBe100()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Quality = 100;

        // Assert
        profile.Quality.Should().Be(100);
    }

    [Fact]
    public void Quality_WhenSetBelow10_ShouldClampTo10()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Quality = 5;

        // Assert
        profile.Quality.Should().Be(10);
    }

    [Fact]
    public void Quality_WhenSetTo10_ShouldBe10()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Quality = 10;

        // Assert
        profile.Quality.Should().Be(10);
    }

    [Fact]
    public void Quality_WhenSetToZero_ShouldClampTo10()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Quality = 0;

        // Assert
        profile.Quality.Should().Be(10);
    }

    [Fact]
    public void Quality_WhenSetToNegative_ShouldClampTo10()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Quality = -50;

        // Assert
        profile.Quality.Should().Be(10);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(50, 50)]
    [InlineData(80, 80)]
    [InlineData(100, 100)]
    public void Quality_WhenSetWithinValidRange_ShouldRetainValue(int inputQuality, int expectedQuality)
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Quality = inputQuality;

        // Assert
        profile.Quality.Should().Be(expectedQuality);
    }

    #endregion

    #region Name Sanitization Tests

    [Fact]
    public void Name_WhenContainsSpaces_ShouldRemoveThem()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Name = "High Quality Profile";

        // Assert
        profile.Name.Should().Be("HighQualityProfile");
    }

    [Fact]
    public void Name_WhenContainsSlashes_ShouldRemoveThem()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Name = "profile/test/name";

        // Assert
        profile.Name.Should().Be("profiletestname");
    }

    [Fact]
    public void Name_WhenContainsSpacesAndSlashes_ShouldRemoveBoth()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Name = "High / Quality / Profile";

        // Assert
        profile.Name.Should().Be("HighQualityProfile");
    }

    [Fact]
    public void Name_WhenHasLeadingAndTrailingSpaces_ShouldTrimThem()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Name = "  ProfileName  ";

        // Assert
        profile.Name.Should().Be("ProfileName");
    }

    [Fact]
    public void Name_WhenSetToNull_ShouldRemainEmpty()
    {
        // Arrange
        var profile = new VideoProfile();
        profile.Name = "InitialName";

        // Act
        profile.Name = null!;

        // Assert - Should retain previous value since null/empty check prevents assignment
        profile.Name.Should().Be("InitialName");
    }

    [Fact]
    public void Name_WhenSetToEmptyString_ShouldRemainPreviousValue()
    {
        // Arrange
        var profile = new VideoProfile();
        profile.Name = "InitialName";

        // Act
        profile.Name = "";

        // Assert - Should retain previous value since null/empty check prevents assignment
        profile.Name.Should().Be("InitialName");
    }

    [Fact]
    public void Name_WhenSetToValidString_ShouldSetCorrectly()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Name = "ValidName";

        // Assert
        profile.Name.Should().Be("ValidName");
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void Height_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Height = 1080;

        // Assert
        profile.Height.Should().Be(1080);
    }

    [Fact]
    public void Width_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.Width = 1920;

        // Assert
        profile.Width.Should().Be(1920);
    }

    [Fact]
    public void MaxBitrate_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.MaxBitrate = 8000000;

        // Assert
        profile.MaxBitrate.Should().Be(8000000);
    }

    [Fact]
    public void MinBitrate_WhenSet_ShouldReturnSetValue()
    {
        // Arrange
        var profile = new VideoProfile();

        // Act
        profile.MinBitrate = 250000;

        // Assert
        profile.MinBitrate.Should().Be(250000);
    }

    #endregion
}
