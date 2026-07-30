using KnowledgeHub.Contracts;

namespace IntegrationTests;

public sealed class ContractTests
{
    [Fact]
    public void Citation_url_targets_document_and_chunk()
    {
        var document = Guid.NewGuid(); var chunk = Guid.NewGuid();
        var citation = new Citation(chunk, document, "guide.pdf", 2, "Install", .91, "snippet", $"/documents/{document}#chunk-{chunk}");
        Assert.Contains(document.ToString(), citation.Url);
        Assert.Contains(chunk.ToString(), citation.Url);
    }
}
