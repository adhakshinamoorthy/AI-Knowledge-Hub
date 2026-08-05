# Contributing

Preserve tenant filters in relational and vector queries, keep uploaded file paths server-generated, keep model integrations behind application ports, and do not add live external dependencies to deterministic tests. Document model, vector-size, contract, or operational changes.

Before opening a pull request, run:

```powershell
dotnet build AIKnowledgeHub.slnx -c Release --warnaserror
dotnet test AIKnowledgeHub.slnx -c Release --no-build
docker compose config --quiet
```

