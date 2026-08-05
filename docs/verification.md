# Verification

CI performs a warning-free Release build, deterministic tests, NuGet audit, and Compose schema validation.

For an end-to-end check, start the dependencies, pull both Ollama models, upload a uniquely identifiable document, poll until `Indexed`, search for its content, and verify chat citations point to its chunks. Repeat list, search, retrieval, and deletion calls with a second tenant token and confirm no document, vector, conversation, or citation crosses the tenant boundary. Inspect the dead-letter queue by forcing one unsupported or failed processing job.

