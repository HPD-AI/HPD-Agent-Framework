# Zoom-like Playground (WebRTC)

This playground contains a minimal Zoom-like app with **real audio/video** using WebRTC.
It supports **1:1 calls** (two participants in the same room) with basic controls.

## Structure

- `client/` - React (Vite) app
- `server/` - WebSocket signaling server

## Prerequisites

- Node.js 18+

## Run the signaling server

```bash
cd Playground/server
npm install
npm run start
```

The server listens on `ws://localhost:8080`.

## Run the React client

```bash
cd Playground/client
npm install
npm run dev
```

Open the URL shown by Vite (typically `http://localhost:5173`).

## Usage

- Enter a room ID (e.g., `demo`) and click **Join** in two different browser tabs or devices.
- Grant camera/microphone permissions.
- You should see local and remote video streams.

## Notes

- This is a **1:1** WebRTC demo. Multiparty requires an SFU/MCU or more peer management.
- Works best on `localhost` or HTTPS (browsers require secure contexts for camera/mic).
