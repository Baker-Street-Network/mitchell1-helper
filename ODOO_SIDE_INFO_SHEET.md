# Odoo Side Info Sheet (for LLM Implementation)

This document describes exactly what the Odoo side must provide so `Mitchell1Helper` can sync correctly.

## 1. Integration Goal

Implement the Odoo side that:

1. Supports standard Odoo JSON-RPC access over `/jsonrpc`.
2. Publishes websocket notifications over `/websocket` when records change.
3. Uses channel `record_updated` (default expected by helper).
4. Allows helper to fetch changed records (`res.partner` currently) and mirror them into SQL Server.
6. Accepts real-time heartbeat pushes from Mitchell1Helper over websocket so Odoo can track agent liveness.

The helper already does all client-side polling/listening logic.

## 2. What the C# Helper Currently Expects

Current behavior in helper:

1. Connect websocket to: `ws(s)://<odoo-host>/websocket`.
2. Sends this subscribe payload:

```json
{
  "event_name": "subscribe",
  "data": {
    "channels": ["<optional secret>", "record_updated"],
    "last": 0
  }
}
```

Notes:

1. Secret channel is optional. If configured in helper settings, it is included before `record_updated`.
2. `last` is the last message id already seen; helper uses this for resume semantics.

On incoming messages, helper expects a JSON array of notification objects, each with:

1. `id` (message id, integer)
2. `message.payload` object

Helper parses payload keys:

1. `model` (optional but recommended)
2. `id` (single record id)
3. `customer_id` (single record id)
4. `ids` (array of ids)

If `model` exists, helper only processes it when it equals configured model (default: `res.partner`).

## 3. Required Odoo Record Fields

When helper fetches a changed customer, it calls standard JSON-RPC `execute_kw` on model `res.partner` with `search_read` and expects fields:

1. `id`
2. `name`
3. `email`
4. `phone`
5. `mobile`
6. `active`
7. `ref`
8. `write_date`

The helper maps:

1. `active == false` -> SQL `IsDeleted = true`
2. `write_date` -> SQL `LastChangeDate`
3. `ref` must map to SQL id via prefix rule

## 4. Critical Mapping Contract (ref)

Helper resolves SQL CustomerID from Odoo `ref` using:

1. Prefix: `MITCHELL1-` (configurable in helper)
2. Format: `MITCHELL1-<numeric_id>`

Example:

1. `ref = "MITCHELL1-12345"` -> SQL CustomerID = `12345`

If `ref` is missing, wrong prefix, or non-numeric suffix, helper skips the record.

## 5. Standard JSON-RPC Calls Used by Helper

Authentication:

1. Service: `common`
2. Method: `authenticate`

Object methods:

1. Service: `object`
2. Method: `execute_kw`

Current calls:

1. `search_read` (find by `id`)
2. `search_read` (find by `ref`)
3. `write`
4. `create`

No custom RPC endpoint is required; standard Odoo JSON-RPC is used.

## 6. Websocket Notification Payload Contract

For each changed record, publish payload compatible with one of these patterns:

### Preferred

```json
{
  "model": "res.partner",
  "ids": [123, 456]
}
```

### Also accepted

```json
{
  "model": "res.partner",
  "id": 123
}
```

or

```json
{
  "model": "res.partner",
  "customer_id": 123
}
```

Envelope should resemble:

```json
[
  {
    "id": 10001,
    "message": {
      "payload": {
        "model": "res.partner",
        "ids": [123]
      }
    }
  }
]
```

## 6.1 Heartbeat Push Contract (Helper -> Odoo)

Mitchell1Helper now opens an outbound websocket connection to `/websocket` and sends heartbeat frames.

Heartbeat payload shape:

```json
{
  "event_name": "heartbeat",
  "data": {
    "channel": "mitchell1_status",
    "app": "Mitchell1Helper",
    "version": "<assembly-version>",
    "machine_name": "<windows-hostname>",
    "status": "running",
    "utc_now": "2026-05-27T17:00:00.0000000Z",
    "last_successful_poll_utc": "2026-05-27T16:59:30.0000000Z",
    "last_error": null,
    "secret": "<optional-secret-if-configured>"
  }
}
```

Notes:

1. `secret` is only sent when configured in helper settings.
2. First heartbeat is sent immediately after websocket connect.
3. Subsequent heartbeats are sent every `HeartbeatIntervalSeconds` (default 15s).
4. Odoo should treat missing heartbeats as disconnected/offline agent.

## 7. Odoo Module Responsibilities

Implement an Odoo addon that:

1. Hooks partner create/write/archive events.
2. Emits websocket event on `record_updated` channel with changed partner ids.
3. Optionally emits on a secret channel too, if your deployment requires gated listeners.
4. Ensures `ref` is maintained in required format for records that should sync.
5. Listens for `heartbeat` websocket messages and updates an in-memory or persisted agent status view.

For archive/delete semantics:

1. Use Odoo `active = False` (do not hard delete).
2. Keep `write_date` updating on relevant changes.

## 8. Suggested Odoo Implementation Approach

1. Add a lightweight service/helper in addon to publish bus/websocket events.
2. Override `create` and `write` in `res.partner` extension.
3. When relevant fields change, publish one notification with all affected IDs.
4. Ensure payload includes `model` and `ids`.
5. If using a secret channel, include both channels in your publish strategy.

## 9. Security and Permissions

1. Create a dedicated API user in Odoo with access to `res.partner` fields above.
2. Restrict permissions to required models/fields only.
3. Use HTTPS/WSS in production.
4. If using channel secrets, rotate and store securely.

## 10. Test Checklist (must pass)

1. JSON-RPC auth works with API user.
2. `search_read` for `res.partner` returns all required fields.
3. Websocket subscribe on `record_updated` succeeds.
4. Partner update in Odoo emits payload with `model` and IDs.
5. Helper receives event, fetches partner, and updates SQL row.
6. Archiving partner (`active=False`) propagates to SQL `IsDeleted=true`.
7. `ref` mapping failures are logged and skipped safely.
8. Reconnect/resume works using message `id` and subscribe `last`.

## 11. Known Current Constraints in Helper

1. Reverse websocket sync is currently customer-specific (`res.partner`) even though channel name is generic.
2. SQL upsert assumes SQL columns: `Name`, `Email`, `Phone`, `Mobile`, plus configured id/change/delete columns.
3. Outbound polling from SQL to Odoo is still customer-focused.

## 12. Copy-Paste LLM Prompt for Odoo Work

Use this prompt when asking an LLM to generate the Odoo addon:

"Build an Odoo 19 addon that publishes websocket bus notifications on channel `record_updated` whenever `res.partner` records are created, updated, or archived. Payload must include `model: res.partner` and `ids: [<record_ids>]`. Ensure standard Odoo JSON-RPC works for authenticate and execute_kw search_read on fields id,name,email,phone,mobile,active,ref,write_date. Keep partner `ref` in format `MITCHELL1-<numeric_sql_id>` for synced records. Do not hard-delete; use `active=False`. Include minimal tests and clear module structure."
