# ADR 0001: Asynchronous document ingestion

Status: Accepted

## Decision

Persist document and processing-job state before publishing a durable RabbitMQ message. A separate worker parses, chunks, embeds, and indexes the document, acknowledging only successful delivery and dead-lettering failures.

## Consequences

Uploads return quickly, processing can scale independently, and failures are visible through document state. Publishing is not yet protected by a transactional outbox, so a process failure between database commit and message publication remains a reliability gap to address before production use.

