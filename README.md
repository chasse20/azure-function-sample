# Azure Function Sample

This repository is a sanitized code sample from a larger professional .NET Azure Functions service.

The sample focuses on a serverless document lifecycle built around Azure Functions, Durable Functions, EF Core/SQL Server, DocuSign, Azure Blob Storage, Key Vault, and SendGrid.

- .NET 10 Azure Functions isolated worker
- Durable Functions orchestration and deterministic instance IDs
- EF Core with pooling for MSSQL
- DocuSign preview generation using a temporary envelope and combined PDF download
- DocuSign JWT authentication, token caching, retry/backoff, envelope creation, voiding, document download, and cleanup
- SendGrid templated email integration
- Webhook based envelope state transitions
- Transient failure handling
- Azure Blob Storage uploads, conditional writes, ETag-aware copies/deletes, metadata, and post-commit cleanup
- Azure Key Vault integration
- OpenTelemetry/Azure Monitor integration

## Flow

```text
Envelope + SQL metadata
        |
        v
Azure Function
        |
        v
Durable orchestration
        |
        +--> Azure Blob documents
        |
        +--> DocuSign envelope
        |
        +--> optional PDF preview -> Blob + SQL File record
        |
        v
DocuSign webhook
        |
        +--> update envelope state in SQL
        +--> remove stale preview
        +--> archive completed PDF to Blob Storage
        +--> SendGrid notification
```

## Public-copy scope

The original service used the same EF Core/SQL Server persistence pattern shown here, but against a much larger business schema. Production configuration, Azure DevOps pipelines, service-dependency metadata, internal messaging integrations, company-specific document/tagging rules, and unrelated application functions are not included.

The webhook endpoint retains Function-level Azure authorization from the source design. Before using a similar endpoint on the public internet, configure and validate the appropriate DocuSign Connect HMAC/signature mechanism for the deployment.

## Configuration

`local.settings.example.json` documents the expected settings. SendGrid and DocuSign secret material is loaded from Azure Key Vault. SQL and Blob settings contain only local/example values.