# Architecture

## System context

AI Knowledge Hub ingests tenant documents and exposes semantic search and grounded chat through a secured API.

```text
Client -> API -> PostgreSQL (documents, jobs, conversations, audit)
             |-> file storage
             +-> RabbitMQ -> Worker -> parser -> Ollama embeddings -> Qdrant
Client <- SSE chat <- API -> retrieval -> Ollama chat
API + Worker -> Redis / OTLP
```

## Data ownership and trust

PostgreSQL is the workflow system of record. Qdrant contains rebuildable chunk vectors and tenant metadata. Files are stored under server-generated document directories. RabbitMQ decouples ingestion from parsing and embedding; failed deliveries route to a dead-letter queue.

Every document, conversation, and vector operation must include the authenticated `tenant_id`. Retrieved text is untrusted content: it may inform an answer but must not override system policy or authorization. Citations preserve document and chunk identity so users can inspect the evidence.

## Quality boundaries

Deterministic tests validate parsing, chunking, and public contracts. PostgreSQL, RabbitMQ, Qdrant, Redis, Ollama, and end-to-end tenant isolation require the Compose smoke test. `EnsureCreated` is convenient for this reference implementation; production evolution should use reviewed migrations.

