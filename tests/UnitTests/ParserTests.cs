using KnowledgeHub.Infrastructure;

namespace UnitTests;

public sealed class ParserTests
{
    [Fact]
    public async Task Plain_text_parser_extracts_content()
    {
        var path = Path.Combine(Path.GetTempPath(), $"knowledgehub-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "retrieval augmented generation");
            var parsed = await new DocumentParser().ParseAsync(path, CancellationToken.None);
            Assert.Contains("retrieval augmented generation", parsed.Pages.Single().Text);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".md")]
    [InlineData(".txt")]
    public void Parser_reports_supported_extensions(string extension) => Assert.True(new DocumentParser().CanParse(extension));
}
