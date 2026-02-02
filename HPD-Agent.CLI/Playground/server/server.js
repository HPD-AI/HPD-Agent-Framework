import { WebSocketServer } from "ws";

const wss = new WebSocketServer({ port: 8080 });

const rooms = new Map(); // roomId -> Set<ws>

function send(ws, data) {
  if (ws.readyState === ws.OPEN) {
    ws.send(JSON.stringify(data));
  }
}

function broadcast(roomId, sender, data) {
  const peers = rooms.get(roomId);
  if (!peers) return;
  for (const peer of peers) {
    if (peer !== sender) {
      send(peer, data);
    }
  }
}

wss.on("connection", (ws) => {
  ws.roomId = null;

  ws.on("message", (raw) => {
    let msg;
    try {
      msg = JSON.parse(raw.toString());
    } catch {
      return;
    }

    const { type, roomId, payload } = msg;

    if (type === "join") {
      ws.roomId = roomId;
      if (!rooms.has(roomId)) rooms.set(roomId, new Set());
      rooms.get(roomId).add(ws);
      // Notify others someone joined
      broadcast(roomId, ws, { type: "peer-joined" });
      return;
    }

    if (!ws.roomId) return;

    switch (type) {
      case "offer":
      case "answer":
      case "ice":
        broadcast(ws.roomId, ws, { type, payload });
        break;
      case "leave":
        broadcast(ws.roomId, ws, { type: "peer-left" });
        break;
      default:
        break;
    }
  });

  ws.on("close", () => {
    if (!ws.roomId) return;
    const peers = rooms.get(ws.roomId);
    if (peers) {
      peers.delete(ws);
      if (peers.size === 0) rooms.delete(ws.roomId);
      else broadcast(ws.roomId, ws, { type: "peer-left" });
    }
  });
});

console.log("Signaling server running on ws://localhost:8080");
