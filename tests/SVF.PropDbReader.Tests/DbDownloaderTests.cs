using SVF.PropDbReader;

namespace SVF.PropDbReader.Tests;

/// <summary>
/// Tests for DbDownloader.SanitizeFilename.
/// </summary>
public class DbDownloaderTests
{
    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData("ABC123", "abc123")]
    [InlineData("a-b-c", "a_b_c")]
    [InlineData("a/b/c", "a_b_c")]
    [InlineData("urn:adsk.viewing:fs.file:dXJuOm", "urn_adsk_viewing_fs_file_dxjuom")]
    [InlineData("", "")]
    public void SanitizeFilename_ReturnsExpectedResult(string input, string expected)
    {
        var result = DbDownloader.SanitizeFilename(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFilename_OnlyContainsLowercaseAlphanumericAndUnderscore()
    {
        var result = DbDownloader.SanitizeFilename("Hello!@#$%^&*()World_123");

        foreach (char c in result)
        {
            Assert.True(char.IsLetterOrDigit(c) || c == '_',
                $"Unexpected character '{c}' in sanitized filename");
            if (char.IsLetter(c))
                Assert.True(char.IsLower(c), $"Character '{c}' should be lowercase");
        }
    }

    [Fact]
    public void SanitizeFilename_PreservesLength()
    {
        string input = "abcDEF123!@#";
        var result = DbDownloader.SanitizeFilename(input);
        Assert.Equal(input.Length, result.Length);
    }
}
