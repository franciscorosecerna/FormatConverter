using FormatConverter;
using Xunit;

namespace FormatConverter.Tests
{
    public class ProgramTests
    {
        [Theory]
        [InlineData("json", ".json")]
        [InlineData("xml", ".xml")]
        [InlineData("yaml", ".yaml")]
        [InlineData("protobuf", ".pb")]
        [InlineData("unknown", ".out")]
        public void GetFileExtension_ReturnsExpectedExtension(string format, string expected)
        {
            var result = Program.GetFileExtension(format);
            Assert.Equal(expected, result);
        }
    }
}
