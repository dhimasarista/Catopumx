# Catopumx

**A single-process, deterministic realtime hub for Industrial IoT (IIoT), built on .NET.**

Catopumx collapses the typical IIoT ingestion stack — MQTT broker, worker, database, web server — into one process, so an edge gateway or small VPS can ingest, deduplicate, persist, and broadcast telemetry without an orchestration layer. It also bridges legacy Modbus TCP devices onto that same pipeline, so PLCs and sensors that don't speak MQTT natively don't need a separate gateway.

> **Project status: early / actively developed.** The core pipeline (MQTT ingest → dedup → vault → SSE → alerts) is implemented and covered by unit tests. It has not been run against real factory hardware or under production load.

## Vision

Industrial environments need strict data determinism, zero duplication, and real-time visualization, without wiring together a broker, a worker process, a database, and a web server by hand. Catopumx's design:

1. **Embedded MQTT Ingestor** — devices connect directly to Catopumx; no external broker to run.
2. **Modbus TCP → MQTT Bridge** — legacy PLCs and sensors that only speak Modbus are polled and republished as MQTT, so they flow through the same pipeline as native MQTT devices.
3. **Deterministic Vault** — idempotent writes to Postgres/MySQL/SQLite so duplicate telemetry (from flaky factory networks or repeated Modbus polls) never lands twice.
4. **Real-time Broadcaster** — built-in Server-Sent Events (SSE) so dashboards can stream live data without hitting the database.
5. **Threshold Alerting** — simple rules re-publish to MQTT or POST a webhook when a value crosses a configured threshold.

## Architecture

```mermaid
flowchart LR
    subgraph Edge ["Edge Layer"]
        MqttDevice["Native MQTT Device"]
        Plc["Modbus TCP PLC / Sensor"]
    end

    subgraph Catopumx ["Catopumx Middleware"]
        Bridge["Modbus -> MQTT Bridge"]
        Broker["Embedded MQTT Broker"]
        Engine["Ingestion & Dedup Engine"]
        Alerts["Alert Engine"]
        Cache["In-Memory State Cache"]
        SSE["SSE Broadcaster"]

        Plc --> Bridge
        Bridge -- "publish" --> Broker
        MqttDevice -- "publish" --> Broker
        Broker -- "intercept" --> Engine
        Engine --> Cache
        Engine --> Alerts
        Engine --> SSE
        Alerts -- "publish" --> Broker
    end

    subgraph Storage ["Storage Layer"]
        DB[("Postgres / MySQL / SQLite")]
    end

    subgraph Frontend ["Presentation Layer"]
        ClientApp["Dashboard / Webhook Receiver"]
    end

    Engine -- "idempotent UPSERT" --> DB
    SSE -- "text/event-stream" --> ClientApp
    Alerts -- "webhook POST" --> ClientApp
```

## Project Layout

```
Catopumx/
  Program.cs               Composition root: config load, broker bootstrap, HTTP endpoints
  Configuration/            catopumx.json model + parsing (AppConfig, ModbusDevice, AlertRule, Operator)
  Mqtt/                     Embedded broker bootstrap and in-process publish helper
  Modbus/                   Modbus TCP -> MQTT polling bridge
  Storage/                  Vault: per-backend (Postgres/MySQL/SQLite) schema + upsert SQL
  Alerting/                 Threshold evaluation (AlertEngine) and dispatch (AlertDispatcher)
  Ingestion/                Dedup cache (StateCache), SSE fan-out (EventBus), the core pipeline (Ingest)
  Catopumx.Tests/           Unit tests, one file per area above
```

## Tech Stack

| Concern | Library |
|---|---|
| HTTP server | ASP.NET Core Minimal API |
| Embedded MQTT broker | [MQTTnet](https://github.com/dotnet/MQTTnet) |
| Modbus TCP client | [FluentModbus](https://github.com/Apollo3zehn/FluentModbus) |
| Database access | [Npgsql](https://www.npgsql.org/) / [MySqlConnector](https://mysqlconnector.net/) / [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) behind a small `Backend` abstraction |
| In-memory state cache | `ConcurrentDictionary` (built-in) |
| Multi-consumer event broadcast | Custom `EventBus` (per-subscriber bounded `Channel<T>`) |
| Webhook dispatch | `HttpClient` (built-in) |
| Config | Hand-rolled `.env` loader, `System.Text.Json` (`catopumx.json`) |
| Serialization | `System.Text.Json` (built-in) |
| Logging | `Microsoft.Extensions.Logging` (built-in) |

## Getting Started

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) 10.0+
- Optionally, a Postgres, MySQL, or SQLite database if you want persistence instead of No-DB mode
- Optionally, a Modbus TCP device (or simulator) if you want to use the bridge

### Build & Run

```bash
git clone https://github.com/dhimasarista/Catopumx.git
cd Catopumx

# Copy the example environment file and adjust as needed
cp .env.example .env

# Optional: enable Modbus devices and/or alert rules
cp catopumx.json.example catopumx.json

# Run in debug mode
dotnet run

# Or build and run a release binary
dotnet build -c Release
dotnet bin/Release/net10.0/Catopumx.dll
```

On startup, Catopumx logs whether it connected to a database or is running in No-DB mode, how many Modbus devices and alert rules were loaded from `catopumx.json`, then starts the MQTT broker (`MQTT_LISTEN_ADDR`) and the HTTP broadcaster (`http://0.0.0.0:3000`).

### Verify it's running

```bash
curl http://localhost:3000/health
# {"status":"ok","database":"No-DB Mode","modbus_devices":0,"alert_rules":0}
```

### Run the tests

```bash
cd Catopumx.Tests
dotnet test
```

## Configuration

Catopumx reads two files: `.env` for runtime/environment settings, and `catopumx.json` (optional) for Modbus devices and alert rules. See [`.env.example`](.env.example) and [`catopumx.json.example`](catopumx.json.example) for the full reference.

| Variable | Required | Default | Description |
|---|---|---|---|
| `DATABASE_URL` | No | *(unset)* | Postgres/MySQL/SQLite connection string. Omit to run in No-DB (broadcast-only) mode. |
| `MQTT_LISTEN_ADDR` | No | `0.0.0.0:1883` | Address the embedded MQTT broker listens on. |
| `MQTT_USERNAME` / `MQTT_PASSWORD` | No | *(unset)* | Credentials required from MQTT clients. Unset means unauthenticated. |
| `HTTP_LISTEN_ADDR` | No | `0.0.0.0:3000` | Address the HTTP broadcaster listens on. |

`catopumx.json` has two top-level array fields, both optional and independent: `modbus` (Modbus TCP devices to poll and bridge onto MQTT) and `alerts` (threshold rules evaluated against ingested JSON payloads). Neither file is required to start Catopumx.

## API Reference

| Method | Path | Description |
|---|---|---|
| `GET` | `/health` | Process health, DB status, Modbus device count, alert rule count |
| `GET` | `/api/stream` | Server-Sent Events stream of every deduplicated ingested message |
| `GET` | `/api/state` | JSON snapshot of the latest value for every known topic |
| `GET` | `/api/state/{topic}` | Latest value for one topic (`404` if never seen) |

## Security posture (read before exposing beyond localhost)

This is an early-stage project; the following are known, deliberate gaps rather than oversights:

- **MQTT broker has no authentication unless you set it.** Set both `MQTT_USERNAME` and `MQTT_PASSWORD` before exposing `MQTT_LISTEN_ADDR` beyond localhost or a trusted network segment.
- **The HTTP API has no authentication at all.** It's read-only, but discloses every ingested topic's current value to anyone who can reach port 3000. Put it behind a reverse proxy with auth, or a network ACL.
- **The state cache caps topic handling** at 10,000 distinct topics, and topics longer than 255 bytes are rejected outright — a circuit breaker against an unauthenticated publisher exhausting memory with unique topics.
- **Webhook calls have a 10s timeout** and the configured URL is redacted before it's logged, but the URL/payload themselves aren't validated.
- **No TLS** on the MQTT broker or the HTTP server. Fine on a trusted LAN or behind a VPN; put a TLS-terminating proxy in front of both before crossing an untrusted network.

## Roadmap

- [ ] Per-topic dynamic schema inference (deliberately not implemented — the vault stores every topic's latest payload as an opaque JSON string in one fixed table rather than generating per-field typed columns from untrusted topic names/JSON keys)
- [ ] Persistent (rather than reconnect-per-poll) Modbus TCP sessions for high-frequency polling
- [ ] MQTT wildcard (`+`/`#`) support in alert rule topic matching
- [ ] Historical time-series storage (currently only the *latest* value per topic is persisted)
- [ ] Integration tests against a real Modbus simulator and a real Postgres/MySQL instance
- [ ] CI workflow (`dotnet build`, `dotnet test`)
- [ ] Document deployment (systemd/Windows service or container image) for edge gateways
- [ ] License

## Contributing

This project is in early, active development. Issues and pull requests are welcome via [GitHub](https://github.com/dhimasarista/Catopumx).

## License

No license has been declared for this project yet. All rights reserved by the author until a license is added.

---

*Built for the industrial edge.*
