# BGPLite

Lightweight BGP route server with dynamic prefix provisioning via RIPE Stat and HTTP management API.

Built with .NET 10. Peers register through the API with subscriptions to AS-lists (Cloudflare, Google, Apple, Meta, etc.) or custom prefixes. On BGP connection, prefixes are fetched from RIPE Stat with caching and advertised to the peer.

## Features

- BGP session management (OPEN, UPDATE, KEEPALIVE, NOTIFICATION)
- 4-byte ASN support
- Dynamic prefix provisioning via RIPE Stat API with in-memory caching
- Per-peer AS-list subscriptions and custom prefix support
- Configurable prefix sources (local file / HTTP / ...) via a provider factory, with in-memory caching
- Auto-registration of unknown peers with default RU prefix set
- HTTP management API for peer and route management
- SQLite peer store via EF Core
- Docker support

## Requirements

- .NET 10.0 SDK
- Linux (BGP port 179 requires root or `CAP_NET_BIND_SERVICE`)

## Quick Start

Copy the example config and edit with your settings:

```bash
cp appsettings.Example.yml appsettings.yml
```

Run:

```bash
sudo dotnet run --project BGPLite
```

Or with Docker:

```bash
docker build -t bgplite .
docker run -d \
  -p 179:179 \
  -p 5000:5000 \
  -v $(pwd)/appsettings.yml:/app/appsettings.yml \
  bgplite
```

## Configuration

```yaml
Bgp:
  Asn: 65444
  RouterId: 10.0.0.1
  KeepAlive: 60
  HoldTime: 180

Peers:
  - Address: 10.0.0.2
    RemoteAsn: 65001
    Description: "example-peer"

RipeStat:
  AsnLists:
    - Name: cloudflare
      Description: "AS13335 Cloudflare Inc."
      Asns: [13335]

    - Name: google
      Description: "AS15169 Google LLC"
      Asns: [15169]

    - Name: apple
      Description: "AS714 Apple Inc."
      Asns: [714]

    - Name: meta
      Description: "AS32934 Facebook, Inc."
      Asns: [32934]

    - Name: ru
      Description: "Russia"
      Country: RU
```

### Prefix Sources

Prefix lists are loaded at startup from configurable sources, selected by `Kind` through a provider factory (`file`, `http`, ...) and kept in an in-memory TTL cache. Each source may attach a BGP community in `ASN:VALUE` form and, for `http`, override the fetch `Timeout` (seconds) and add custom request `Headers` (e.g. `Authorization`, `X-API-Key`). Add a new loading method by implementing `IPrefixSourceProvider` and registering it.

```yaml
PrefixSources:
  - Kind: http
    Name: ru
    Description: "Russia prefixes from a remote list"
    Url: "https://raw.githubusercontent.com/<org>/<repo>/main/ru.txt"   # any direct raw-file URL
    Timeout: 30                                                          # optional, seconds
    Headers:                                                             # optional request headers
      Authorization: "Bearer <token>"
  - Kind: file
    Name: local
    Path: "extra.txt"
    Community: "65444:100"

DefaultPrefixSource: ru   # source served as the RU/default set to unconfigured peers
```

List files are CIDR-per-line (e.g. `2.16.20.0/23`); blank lines and `#` comments are ignored.

### Data Directory

Peer data is stored in SQLite at `$BGPLITE_DATA/bgplite.db` (defaults to `./data`).

## How It Works

1. **Register a peer** via `POST /api/peers` with IP, ASN, AS-list subscriptions, and/or custom prefixes
2. **Peer connects** via BGP to port 179
3. **Session establishes** — the server looks up the peer in the database:
   - If found → fetches prefixes for subscribed AS-lists from RIPE Stat (cached), adds custom prefixes, advertises all to the peer
   - If not found → auto-registers the peer and advertises the default prefix source (RU)
4. **Statistics updated** — peer status set to `active`, session time recorded

## Management API

Available on port 5000.

### Peers

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/my-ip` | Returns client's IP address |
| `POST` | `/api/peers` | Register a new peer |
| `GET` | `/api/peers` | List all peers |

#### Register a Peer

```bash
curl -X POST http://localhost:5000/api/peers \
  -H 'Content-Type: application/json' \
  -d '{
    "ip": "10.0.0.2",
    "asn": 65001,
    "description": "customer-1",
    "asnLists": ["cloudflare", "google"],
    "customPrefixes": ["203.0.113.0/24"]
  }'
```

Response:

```json
{
  "data": {
    "id": "a1b2c3d4-...",
    "ip": "10.0.0.2",
    "asn": 65001,
    "description": "customer-1",
    "status": "inactive",
    "createdAt": "2026-06-09T12:00:00Z",
    "asnLists": ["cloudflare", "google"],
    "customPrefixes": ["203.0.113.0/24"]
  }
}
```

#### List Peers

```bash
curl http://localhost:5000/api/peers
```

Returns peers with `id`, `ip`, `asn`, `description`, `status`, `createdAt`, `lastSessionAt`, `communities`.

### AS Lists

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/asn-lists` | List available AS-lists with prefix counts |
| `GET` | `/api/as/{asn}/prefixes/count` | Get prefix count for a specific ASN |

```bash
curl http://localhost:5000/api/asn-lists
curl http://localhost:5000/api/as/13335/prefixes/count
```

### Sessions & Routes

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/sessions` | Active BGP session count |
| `GET` | `/api/routes/count` | Route counts by community |

### Peer Management (Legacy)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/peer/{ip}/communities` | Get peer community filter |
| `PUT` | `/api/peer/{ip}/communities` | Set peer community filter |
| `DELETE` | `/api/peer/{ip}/communities` | Clear community filter |
| `PUT` | `/api/peer/{ip}/description` | Set peer description |

```bash
# Set community filter
curl -X PUT http://localhost:5000/api/peer/10.0.0.2/communities \
  -H 'Content-Type: application/json' \
  -d '{"communities": ["65444:100"]}'

# Route statistics
curl http://localhost:5000/api/routes/count
```

## Project Structure

```
BGPLite/
├── BGPLite/               # Entry point, host setup, DI
├── BGPLite.Api/           # Management HTTP API, peer store (EF Core)
│   └── Entities/          # EF Core entity models
├── BGPLite.Configuration/ # YAML config loading, AppConfig models
├── BGPLite.Protocol/      # BGP message encoding/decoding
├── BGPLite.Providers/     # PrefixService, RipeStatProvider, prefix source providers + factory
├── BGPLite.Routing/       # Route table, community filters
├── BGPLite.Server/        # TCP listener, BGP session FSM
└── BGPLite.Tests/         # Unit tests
```

## License

MIT
