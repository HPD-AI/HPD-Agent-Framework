import React, { useEffect, useMemo, useRef, useState } from "react";

const SIGNALING_URL = "ws://localhost:8080";
const ICE_SERVERS = [
  { urls: "stun:stun.l.google.com:19302" },
  { urls: "stun:stun1.l.google.com:19302" }
];

function useMediaStream() {
  const [stream, setStream] = useState(null);
  const [error, setError] = useState(null);

  useEffect(() => {
    let active = true;
    navigator.mediaDevices
      .getUserMedia({ video: true, audio: true })
      .then((media) => {
        if (active) setStream(media);
      })
      .catch((err) => setError(err));

    return () => {
      active = false;
      if (stream) {
        stream.getTracks().forEach((t) => t.stop());
      }
    };
  }, []);

  return { stream, error };
}

export default function App() {
  const { stream, error } = useMediaStream();
  const localVideoRef = useRef(null);
  const remoteVideoRef = useRef(null);

  const [roomId, setRoomId] = useState("demo");
  const [joined, setJoined] = useState(false);
  const [status, setStatus] = useState("Not connected");
  const [muted, setMuted] = useState(false);
  const [cameraOff, setCameraOff] = useState(false);

  const socketRef = useRef(null);
  const peerRef = useRef(null);

  const canJoin = useMemo(() => !!stream && !joined, [stream, joined]);

  useEffect(() => {
    if (localVideoRef.current && stream) {
      localVideoRef.current.srcObject = stream;
    }
  }, [stream]);

  useEffect(() => {
    return () => {
      cleanupConnection();
    };
  }, []);

  const cleanupConnection = () => {
    if (socketRef.current) {
      socketRef.current.close();
      socketRef.current = null;
    }
    if (peerRef.current) {
      peerRef.current.close();
      peerRef.current = null;
    }
    setJoined(false);
    setStatus("Disconnected");
  };

  const createPeerConnection = () => {
    const pc = new RTCPeerConnection({ iceServers: ICE_SERVERS });

    if (stream) {
      stream.getTracks().forEach((track) => pc.addTrack(track, stream));
    }

    pc.onicecandidate = (event) => {
      if (event.candidate) {
        socketRef.current?.send(
          JSON.stringify({
            type: "ice",
            roomId,
            payload: event.candidate
          })
        );
      }
    };

    pc.ontrack = (event) => {
      const [remoteStream] = event.streams;
      if (remoteVideoRef.current) {
        remoteVideoRef.current.srcObject = remoteStream;
      }
    };

    pc.onconnectionstatechange = () => {
      setStatus(pc.connectionState);
    };

    return pc;
  };

  const joinRoom = async () => {
    if (!stream) return;

    const socket = new WebSocket(SIGNALING_URL);
    socketRef.current = socket;

    socket.onopen = () => {
      socket.send(JSON.stringify({ type: "join", roomId }));
      setJoined(true);
      setStatus("Waiting for peer...");
    };

    socket.onmessage = async (event) => {
      const msg = JSON.parse(event.data);

      if (!peerRef.current) {
        peerRef.current = createPeerConnection();
      }

      switch (msg.type) {
        case "peer-joined": {
          // create offer when someone joins
          const offer = await peerRef.current.createOffer();
          await peerRef.current.setLocalDescription(offer);
          socket.send(JSON.stringify({ type: "offer", roomId, payload: offer }));
          break;
        }
        case "offer": {
          await peerRef.current.setRemoteDescription(msg.payload);
          const answer = await peerRef.current.createAnswer();
          await peerRef.current.setLocalDescription(answer);
          socket.send(JSON.stringify({ type: "answer", roomId, payload: answer }));
          break;
        }
        case "answer": {
          await peerRef.current.setRemoteDescription(msg.payload);
          break;
        }
        case "ice": {
          if (msg.payload) {
            try {
              await peerRef.current.addIceCandidate(msg.payload);
            } catch (err) {
              console.error("ICE error", err);
            }
          }
          break;
        }
        case "peer-left": {
          if (remoteVideoRef.current) {
            remoteVideoRef.current.srcObject = null;
          }
          if (peerRef.current) {
            peerRef.current.close();
            peerRef.current = null;
          }
          setStatus("Peer left");
          break;
        }
        default:
          break;
      }
    };

    socket.onclose = () => {
      setStatus("Disconnected");
    };
  };

  const leaveRoom = () => {
    socketRef.current?.send(JSON.stringify({ type: "leave", roomId }));
    cleanupConnection();
  };

  const toggleMute = () => {
    if (!stream) return;
    stream.getAudioTracks().forEach((t) => (t.enabled = muted));
    setMuted((prev) => !prev);
  };

  const toggleCamera = () => {
    if (!stream) return;
    stream.getVideoTracks().forEach((t) => (t.enabled = cameraOff));
    setCameraOff((prev) => !prev);
  };

  return (
    <div className="app">
      <header className="header">
        <div>
          <h1>Zoom Playground</h1>
          <p className="status">Status: {status}</p>
        </div>
        <div className="controls">
          <input
            value={roomId}
            onChange={(e) => setRoomId(e.target.value)}
            placeholder="Room ID"
            disabled={joined}
          />
          <button onClick={joinRoom} disabled={!canJoin}>
            Join
          </button>
          <button onClick={leaveRoom} disabled={!joined}>
            Leave
          </button>
        </div>
      </header>

      {error && (
        <div className="error">Camera/mic error: {error.message}</div>
      )}

      <section className="videos">
        <div className="video-card">
          <h2>You</h2>
          <video ref={localVideoRef} autoPlay playsInline muted />
        </div>
        <div className="video-card">
          <h2>Remote</h2>
          <video ref={remoteVideoRef} autoPlay playsInline />
        </div>
      </section>

      <footer className="footer">
        <button onClick={toggleMute} disabled={!joined}>
          {muted ? "Unmute" : "Mute"}
        </button>
        <button onClick={toggleCamera} disabled={!joined}>
          {cameraOff ? "Start Video" : "Stop Video"}
        </button>
      </footer>
    </div>
  );
}
