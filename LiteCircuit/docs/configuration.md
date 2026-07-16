# Configuration

Everything lives in `src/LiteCircuit.Web/appsettings.json` (or environment variables with the standard
`Section__Key` syntax, e.g. `Ai__ApiKey`).

## Database

```jsonc
"Database": { "Provider": "Sqlite" },   // Sqlite | SqlServer | Postgres | MySql
"ConnectionStrings": {
  "Sqlite":    "Data Source=litecircuit.db",
  "SqlServer": "Server=localhost;Database=LiteCircuit;Trusted_Connection=True;TrustServerCertificate=True",
  "Postgres":  "Host=localhost;Database=litecircuit;Username=postgres;Password=postgres",
  "MySql":     "Server=localhost;Database=litecircuit;User=root;Password=root"
}
```

- The schema is created automatically on first start (`EnsureCreated`) and the catalog is seeded.
- **MySQL note:** the Pomelo provider currently ships for EF Core 9 while the app uses EF Core 10;
  restore succeeds with a compatibility warning (NU1608). SQLite/SQL Server/PostgreSQL run on
  first-party EF 10 providers. Pin Pomelo to its EF-10 release when it ships.

## Storage (uploads: chat attachments, avatars)

```jsonc
"Storage": {
  "Provider": "FileSystem",             // FileSystem | AzureBlob | S3 | MinIO
  "FileSystem": { "Path": "wwwroot/uploads", "PublicBase": "/uploads" },
  "AzureBlob":  { "ConnectionString": "...", "Container": "litecircuit" },
  "S3": {
    "AccessKey": "...", "SecretKey": "...",
    "Bucket": "litecircuit",
    "Region": "us-east-1",              // for AWS S3
    "ServiceUrl": ""                    // set e.g. http://localhost:9000 for MinIO
  }
}
```

`MinIO` is the S3 provider with `ServiceUrl` set (path-style requests + pre-signed URLs are handled automatically).

## AI (Electra)

```jsonc
"Ai": {
  "Provider": "OpenAI",                 // OpenAI | Anthropic | Gemini | Ollama
  "Model": "",                          // empty = provider default
  "ApiKey": "",                         // or per-provider below
  "Endpoint": "",                       // empty = provider default
  "Temperature": 0.7,
  "MaxTokens": 4000,
  "SystemPrompt": "You are Electra ...",// the persona
  "Providers": {
    "OpenAI":    { "Model": "gpt-4o-mini" },
    "Anthropic": { "Model": "claude-sonnet-5", "Endpoint": "https://api.anthropic.com/v1/" },
    "Gemini":    { "Model": "gemini-2.0-flash", "Endpoint": "https://generativelanguage.googleapis.com/v1beta/openai/" },
    "Ollama":    { "Model": "llama3.2", "Endpoint": "http://localhost:11434/v1" }
  }
},
"Tavily": { "ApiKey": "" }              // enables Electra's internet search
```

AI options reload live (options monitor) — no restart needed after editing.

## Auth

ASP.NET Core Identity with cookie auth. Pages: `/account/login`, `/account/register`,
`/account/forgot-password` (shows the reset link directly since no SMTP is configured),
`/account/reset-password`, `/account/profile` (display name, bio, change password).
Seeded admin: `admin@litecircuit.dev` / `Admin123$` — **change or remove it in production** (`DbSeeder.cs`).

## Performance notes

- Blazor Server keeps editors responsive by doing pointer-level work in plain JS modules; only
  load/save/DRC/route cross the SignalR circuit.
- The auto-router caps its grid at 1M cells; very large boards fall back with a message.
- Three.js and fonts load from CDN; pin or self-host them for offline deployments.
