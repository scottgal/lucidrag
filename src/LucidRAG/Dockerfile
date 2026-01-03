# Build Tailwind CSS
FROM node:20-slim AS frontend
WORKDIR /src
COPY src/LucidRAG/package*.json ./
RUN npm ci
COPY src/LucidRAG/tailwind.config.js ./
COPY src/LucidRAG/src/ ./src/
COPY src/LucidRAG/Views/ ./Views/
RUN npm run build:css

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY src/Mostlylucid.DocSummarizer.Core/*.csproj Mostlylucid.DocSummarizer.Core/
COPY src/Mostlylucid.DocSummarizer.Core/release-notes.txt Mostlylucid.DocSummarizer.Core/
COPY src/Mostlylucid.GraphRag/*.csproj Mostlylucid.GraphRag/
COPY src/Mostlylucid.RAG/*.csproj Mostlylucid.RAG/
COPY src/LucidRAG/*.csproj LucidRAG/
RUN dotnet restore LucidRAG/LucidRAG.csproj

# Copy source and publish
COPY src/Mostlylucid.DocSummarizer.Core/ Mostlylucid.DocSummarizer.Core/
COPY src/Mostlylucid.GraphRag/ Mostlylucid.GraphRag/
COPY src/Mostlylucid.RAG/ Mostlylucid.RAG/
COPY src/LucidRAG/ LucidRAG/

# Copy built CSS from frontend stage
COPY --from=frontend /src/wwwroot/css/dist/ LucidRAG/wwwroot/css/dist/

WORKDIR /src/LucidRAG
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime image - Debian-based (required for DuckDB native libs which need glibc)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install libgomp for ONNX runtime
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Create directories
RUN mkdir -p /app/uploads /app/data /app/logs

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD curl -f http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "LucidRAG.dll"]
