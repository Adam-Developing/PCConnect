# PCConnect API Specification v1.0

The PCConnect API has been refactored into a centralized Front Controller architecture to maximize scalability, improve security, and standardise responses. All interactions now use strict HTTP standard responses and standard JSON.

## Base URLs
* **REST HTTP URL**: `http://<your-domain>:3000/api_node`
* **WebSocket URL**: `ws://<your-domain>:3000`

---

## ⚡ Zero-Latency WebSockets (PC Client)

The Windows PC client should maintain a persistent connection to the WebSocket server instead of polling HTTP databases to receive true zero-latency commands.

### 1. Connection & Authentication
Connect to the server using standard `socket.io-client` libraries.
Immediately upon connecting, the client must trigger the `authenticate` event with the API Key and PCName.

**Emit Event:** `authenticate`
* **Payload:** `{"apiKey": "your-api-key", "pcName": "MyDesktop"}`
* **Response:** Server replies with `authenticated` or `auth_error` event.

### 2. Receiving Commands
When a phone or remote device pushes a command to the REST endpoint `/v1/devices/requests/exchange`, the Node server emits an execution command directly to the WebSocket channel without database lag.

**Listening Event:** `execute_command`
* **Payload Structure:** `{"command": "Shut_Down"}`
* **Client Action:** Execute the system command natively immediately upon receipt.

---

## 🌐 Standard REST API (For Mobile Apps)
*(Note: These can still be used exactly as before!)*

---

## Global Headers Required

Almost all endpoints require strong authentication headers. 

1. `X-API-Key`: Your unique user API Key (Obtained from `/auth/login`).
2. `PCName`: The unique string identifier of the PC making the request (Required for all `/v1/devices/*` endpoints).

### Standard JSON Response Format
```json
{
  "success": true,
  "data": { ... }
}
```
**Errors** will always return `HTTP 400` or `HTTP 401` along with:
```json
{
  "error": true,
  "message": "Human readable error description"
}
```

---

## Endpoints

### 1. Authentication
#### `POST /auth/login`
Authenticates a user via Email/Username and Password, returning the API key to be used for subsequent requests.

* **Payload (JSON or Form-Data):** `loginUsername` (String), `loginPassword` (String)
* **Response:** `{"success": true, "data": {"api_key": "YOUR_KEY_HERE"}}`

#### `POST /auth/signup`
*(Not Implemented)*. Creates a new user profile.

---

### 2. Device Management
*(Note: `X-API-Key` and `PCName` are universally required here)*

#### `GET /v1/devices`
Returns a list of all devices registered to the currently authenticated user.
* **Payload:** None.
* **Response:** `{"success": true, "data": {"PCNames": ["MyLappytop", "WorkPC"]}}`

#### `POST /v1/devices`
Registers a new PC identifier. (Handled automatically on any request, but strictly callable).
* **Payload:** None.
* **Response:** `{"success": true, "data": {"message": "PC added successfully"}}`

#### `GET /v1/system/checkinternet`
A basic ping endpoint functionally identical to older internet checks.
* **Response (HTTP 200):** `"Pong"`

---

### 3. Remote Requests

#### `GET /v1/devices/requests`
Polls for execution requests specifically ordered by an external command application.
* **Response:** `{"success": true, "data": {"request": "command_string_here"}}` OR `{"success": true, "data": {"request": null}}`

#### `POST /v1/devices/requests/clear`
Clears any pending request strings back to their default `0` state for the current PC.
* **Payload:** None.
* **Response:** `{"success": true, "data": {"message": "Request cleared properly"}}`

#### `POST /v1/devices/requests/exchange`
Submits a new request for the target PC.
* **Payload (JSON/Form-Data):** `Request` (String)
* **Response:** `{"success": true, "data": {"message": "Success"}}`

---

### 4. Reminders
*(Note: Requires `X-API-Key`)*

#### `GET /v1/reminders`
Lists all active reminders belonging to the user. Returns them decoded properly via AES-256-CBC.
* **Payload:** None
* **Response:** `[{"ID": 1, "Username": 1008, "Date": "23/02/26", "Time": "15:00", "Reminder": "Buy milk", "Completed": 0}]`

#### `POST /v1/reminders`
Creates a new reminder.
* **Payload:** `date` (String, YYYY-MM-DD), `time` (String, HH:MM or HH:MM:SS), `reminder` (String), `completed` (Boolean/Number optional)
* **Response:** `{"success": true, "data": {"message": "Reminder created", "id": 1}}`

#### `PUT /v1/reminders/:id`
Updates an existing reminder.
* **Payload:** `date` (optional), `time` (optional), `reminder` (optional), `completed` (optional)
* **Response:** `{"success": true, "data": {"message": "Reminder updated", "id": 1}}`

#### `POST /v1/reminders/:id/complete`
Toggles or sets the completion status of a reminder.
* **Payload:** `completed` (Boolean/Number)
* **Response:** `{"success": true, "data": {"message": "Reminder completion updated", "id": 1, "completed": 1}}`
