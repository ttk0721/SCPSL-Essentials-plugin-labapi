# SCPSL Essentials plugin

Example plugin built with LabAPI. It exposes HTTP endpoints for the management panel and sends events when players join the server.

## Configuration
Copy `config.yml.example` to `config.yml` and adjust values.

```
panelUrl: "https://panel.twojadomena.pl"
serverId: "server-001"
apiToken: "abc123xyz456"
listenPort: 7878
```

## Usage
Compile with your usual LabAPI build environment. The plugin listens on the configured port and expects the `X-Server-Token` header from the panel. It periodically sends events back to `/api/plugin/events` on the panel.

