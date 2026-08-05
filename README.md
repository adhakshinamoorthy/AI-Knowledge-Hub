# AI Knowledge Hub

[![CI](https://github.com/adhakshinamoorthy/AI-Knowledge-Hub/actions/workflows/ci.yml/badge.svg)](https://github.com/adhakshinamoorthy/AI-Knowledge-Hub/actions/workflows/ci.yml)

Engineering records: [architecture](docs/architecture.md), [asynchronous ingestion ADR](docs/adr/0001-asynchronous-ingestion.md), [verification](docs/verification.md), [security](SECURITY.md), and [contributing](CONTRIBUTING.md).

Local-first Retrieval-Augmented Generation (RAG) on .NET 10. Upload documents, process them asynchronously, store embeddings in Qdrant, search tenant-scoped knowledge, and stream grounded answers from Ollama with source citations.

## Architecture

```text
Client -> ASP.NET Core API -> PostgreSQL + local document storage
              |                         |
              +-> RabbitMQ -> Worker -> Parser -> Chunker -> Ollama embeddings -> Qdrant
              |
              +-> Search/Chat -> Ollama embedding -> Qdrant retrieval -> Ollama chat -> SSE + citations
```

Clean Architecture dependencies point inward: `Api` and `Worker` compose the application; `Application` defines use cases and ports; `Infrastructure` implements PostgreSQL, RabbitMQ, Qdrant, Ollama, parsers, and storage; `Domain` owns entities; `Contracts` owns public records; `Shared` owns cross-cutting primitives.

## Stack

- .NET 10 / C# 14, ASP.NET Core minimal APIs, Worker Service
- EF Core 10 and PostgreSQL 18
- RabbitMQ 4, Qdrant 1.15, Redis 8, Ollama
- OpenTelemetry OTLP, Serilog, health checks, JWT, rate limiting
- FluentValidation, MediatR, xUnit
- Open XML, PdfPig, and plain-text/Markdown parsing

## Repository

```text
src/KnowledgeHub.Api             HTTP, auth, rate limits, streaming
src/KnowledgeHub.Application     ports, chunking, retrieval, RAG
src/KnowledgeHub.Domain          entities and state
src/KnowledgeHub.Infrastructure  persistence and local integrations
src/KnowledgeHub.Worker          durable queue consumer
src/KnowledgeHub.Contracts       API and event contracts
src/KnowledgeHub.Shared          result primitive
tests/UnitTests                  chunker and parser tests
tests/IntegrationTests           public-contract tests
docker                           images and OTLP collector config
```

## Quick start with Docker

Requirements: Docker Desktop with at least 8 GB available RAM. Ollama model downloads require internet once.

```powershell
docker compose up -d postgres redis rabbitmq qdrant ollama otel-collector
docker compose exec ollama ollama pull nomic-embed-text
docker compose exec ollama ollama pull qwen3:4b
docker compose up --build api worker
```

The API is at `http://localhost:8080`, OpenAPI JSON at `/openapi/v1.json` in Development, health at `/health`, and RabbitMQ management at `http://localhost:15672` (`guest` / `guest`).

## Authentication

Every business endpoint requires a JWT signed with `Security:JwtKey`, issuer `knowledgehub`, audience `knowledgehub-api`, and these claims:

- `sub`: user identifier
- `tenant_id`: mandatory tenant boundary
- `role`: optional role

The checked-in key is for local development only. Override it before sharing a machine:

```powershell
$env:Security__JwtKey = '<at-least-32-random-characters>'
```

## APIs

| Method | Route | Purpose |
|---|---|---|
| POST | `/documents/upload` | Multipart single upload (`file`) |
| POST | `/documents/upload/bulk` | Multipart bulk upload |
| GET | `/documents` | List tenant documents |
| GET | `/documents/{id}` | Document and chunks |
| GET | `/documents/status/{id}` | Processing state/failure |
| DELETE | `/documents/{id}` | Delete file and vectors |
| POST | `/documents/reindex` | Queue document IDs |
| GET/POST | `/search` | Semantic retrieval |
| POST | `/chat` | Grounded answer and citations |
| POST | `/chat/stream` | SSE token stream |
| GET/DELETE | `/chat/history` | Conversation messages |

Uploads accept `.pdf`, `.docx`, `.xlsx`, `.md`, and `.txt`, up to 50 MB. Metadata includes document, page, section, chunk ID, tenant, and score. Each citation includes a clickable `/documents/{id}#chunk-{id}` URL.

## Processing and chunking

An upload is persisted before its durable RabbitMQ message is published. The worker manually acknowledges successful jobs; failed jobs are negatively acknowledged for dead-letter routing. Parsing preserves PDF pages, spreadsheet sheet names, Word title/author, and basic file metadata. Text is whitespace-normalized and split into configurable sentence-aware windows with overlap (`Chunking:Size`, `Chunking:Overlap`). Batched Ollama embeddings are written to Qdrant with tenant-filterable payloads.

Document states are `Uploaded`, `Parsing`, `Parsed`, `Embedding`, `Indexed`, `Failed`, and `Deleted`. Poll `/documents/status/{id}` until `Indexed` before searching.

## Search and RAG

Search embeds the query with `Ollama:EmbeddingModel`, runs cosine nearest-neighbor retrieval in the configured Qdrant collection, applies `TopK` and `ScoreThreshold`, and restricts every request by `tenant_id`. Chat supplies retrieved chunks and recent conversation turns to `Ollama:ChatModel`. The system prompt requires context-only answers and numbered sources. `/chat/stream` emits SSE `data:` frames and honors request cancellation.

## Configuration

All settings are in each host's `appsettings.json` and can be overridden with environment variables using `__` separators.

- `ConnectionStrings:Postgres`
- `Storage:Root`
- `Chunking:Size`, `Chunking:Overlap`
- `Search:TopK`, `Search:ScoreThreshold`
- `Ollama:BaseUrl`, `EmbeddingModel`, `ChatModel`
- `Qdrant:BaseUrl`, `Collection`, `VectorSize`
- `RabbitMQ:Host`, `User`, `Password`, `Queue`
- `Security:JwtKey`, `Issuer`, `Audience`

The selected embedding model and `Qdrant:VectorSize` must agree. `nomic-embed-text` uses 768 dimensions.

## Local development without containers for .NET

Start PostgreSQL, RabbitMQ, Qdrant, Redis, and Ollama with Compose, then:

```powershell
dotnet build AIKnowledgeHub.slnx -c Release
dotnet test AIKnowledgeHub.slnx -c Release --no-build
dotnet run --project src/KnowledgeHub.Worker
dotnet run --project src/KnowledgeHub.Api
```

Local host services use `localhost`; containers override hostnames through Compose.

## Observability

The API emits structured request logs, correlation IDs (`X-Correlation-ID`), ASP.NET/HTTP/runtime metrics, and distributed traces through OTLP. The included collector writes telemetry to its debug exporter. `/health` checks the API and PostgreSQL. Worker errors include RabbitMQ delivery tags and document failures are persisted.

## Testing

```powershell
dotnet test AIKnowledgeHub.slnx -c Release
```

Unit coverage verifies chunk overlap/metadata, empty content handling, supported parser types, and real text extraction. Contract tests verify stable citation URLs. External-service behavior is exercised by the manual smoke test below so normal tests remain deterministic.

## Smoke test

1. Start the stack and pull both models.
2. Mint a local JWT containing `sub=developer` and `tenant_id=demo` with the configured issuer, audience, and key.
3. Upload a text file with `Authorization: Bearer <token>` to `/documents/upload`.
4. Poll `/documents/status/{id}`; expect `Indexed` after model warm-up (typically 5-60 seconds).
5. POST `{"query":"...","topK":5}` to `/search`; expect the uploaded filename and scores.
6. POST `{"question":"..."}` to `/chat`; expect an answer, conversation ID, and citations.
7. Repeat with a token for another tenant; expect no cross-tenant document or search results.

## Troubleshooting

- `Failed` after upload: inspect `docker compose logs worker`; the persisted `failureReason` contains the immediate cause.
- Ollama 404: pull the exact configured model names.
- Qdrant vector-size error: delete the local Qdrant volume after correcting `VectorSize`, then recreate the collection/index.
- RabbitMQ connection refused: wait for the broker, then restart API/worker.
- PostgreSQL unavailable during API startup: wait for its health check and restart the API.
- Slow first answer: Ollama is loading the model; later requests are faster.

## Development rules

Keep tenant filters in every persistence/vector query, cancellation tokens on all I/O, domain dependencies inward, secrets outside committed configuration, and external-runtime tests opt-in. Run Release build and tests with warnings treated as errors before committing.
