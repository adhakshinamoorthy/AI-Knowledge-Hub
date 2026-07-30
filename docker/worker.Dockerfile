FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/KnowledgeHub.Worker/KnowledgeHub.Worker.csproj -c Release -o /app --no-self-contained
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "KnowledgeHub.Worker.dll"]
