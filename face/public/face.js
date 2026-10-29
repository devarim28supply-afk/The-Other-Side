import { Room, RoomEvent, Track, createLocalVideoTrack } from "/livekit/livekit-client.esm.mjs";
import { platePoints, projectHead } from "./project.js";

const HEAR_CM = 1200;
const FACE_CM = 46;
const HEX = 0.72;
const DIAMOND = 0;

const stage = document.getElementById("stage");
const state = {
  shape: "hex",
  lift: 80,
  cam: null,
  people: new Map(),
  selfId: "",
  status: "starting",
};

const videos = new Map();
let localEl = null;
const sink = document.createElement("div");
sink.id = "sink";
sink.style.cssText = "position:fixed;left:-400px;top:0;width:320px;height:180px;overflow:hidden;pointer-events:none";
document.body.appendChild(sink);
let room = null;
let connecting = false;
let publishedId = "";

function inHost() {
  return Boolean(window.chrome?.webview);
}

function report() {
  const dpr = window.devicePixelRatio || 1;
  const faces = [];
  for (const el of stage.querySelectorAll(".plate")) {
    const pts = el._pts;
    if (!pts) continue;
    faces.push(pts.flatMap(([x, y]) => [Math.round(x * dpr), Math.round(y * dpr)]));
  }
  const msg = { status: state.status, faces };
  if (inHost()) window.chrome.webview.postMessage(msg);
}

function setStatus(text) {
  state.status = text;
  report();
}

let watchId = "";
let watchedEl = null;
let meshXY = null;
let meshUV = null;
let meshW = 0;
let meshH = 0;

function showWatch() {
  watchedEl = watchId ? videoFor(watchId) : null;
}

function setMesh(xy, uv, w, h) {
  meshXY = xy;
  meshUV = uv;
  meshW = w | 0;
  meshH = h | 0;
}

window.chrome?.webview?.addEventListener("message", (ev) => {
  const m = ev.data || {};
  if (m.shape === "hex" || m.shape === "diamond") state.shape = m.shape;
  if (typeof m.lift === "number" && m.lift >= 0 && m.lift < 300) state.lift = m.lift;
  if (m.clear) {
    meshXY = null;
  }
  if (Array.isArray(m.xy) && Array.isArray(m.uv) && m.xy.length >= 12 && m.uv.length >= 12) {
    setMesh(m.xy, m.uv, m.w, m.h);
  }
  if (typeof m.watch === "string") {
    watchId = m.watch;
    showWatch();
  }
  if (m.start) connect();
});

function onLine(line) {
  const f = line.split("\t");
  if (f[0] === "L" && f.length >= 5) {
    state.cam = {
      x: +f[1], y: +f[2], z: +f[3], yaw: +f[4],
      pitch: f.length >= 6 ? +f[5] : 0,
      fov: f.length >= 7 ? +f[6] : 90,
    };
    return;
  }
  if (f[0] !== "P" || f.length < 7) return;
  const id = f[1];
  const self = f[6] === "1";
  state.people.set(id, {
    name: f[2], x: +f[3], y: +f[4], z: +f[5], self, t: performance.now(),
  });
  if (self) {
    state.selfId = id;
    publishId();
  }
}

function feed() {
  const es = new EventSource("/feed");
  es.onmessage = (ev) => onLine(ev.data);
  es.onerror = () => setStatus("waiting for the reader feed");
}

async function connect() {
  if (room || connecting) return;
  connecting = true;
  setStatus("joining LiveKit");
  try {
    const identity = localStorage.getItem("tosFaceId") || crypto.randomUUID();
    localStorage.setItem("tosFaceId", identity);
    const tokenRes = await fetch(`/api/token?identity=${encodeURIComponent(identity)}&name=${encodeURIComponent("Other Side")}`);
    if (!tokenRes.ok) {
      const body = await tokenRes.json().catch(() => ({}));
      throw new Error(body.error || `token ${tokenRes.status}`);
    }
    const { token, url } = await tokenRes.json();
    room = new Room({ adaptiveStream: false, dynacast: false });
    room.on(RoomEvent.TrackSubscribed, (track, pub, participant) => {
      if (track.kind !== Track.Kind.Video || pub.source !== Track.Source.Camera) return;
      const el = track.attach();
      el.autoplay = true;
      el.muted = true;
      el.playsInline = true;
      el.style.width = "320px";
      el.style.height = "180px";
      sink.appendChild(el);
      el.play().catch(() => {});
      videos.set(participant.identity, { el, tosId: participant.attributes?.tosId || "" });
      showWatch();
    });
    room.on(RoomEvent.TrackUnsubscribed, (track, _pub, participant) => {
      track.detach().forEach((el) => el.remove());
      videos.delete(participant.identity);
      showWatch();
    });
    room.on(RoomEvent.ParticipantAttributesChanged, (_changed, participant) => {
      const row = videos.get(participant.identity);
      if (row) row.tosId = participant.attributes?.tosId || "";
      showWatch();
    });
    room.on(RoomEvent.Disconnected, () => {
      room = null;
      videos.clear();
      setStatus("LiveKit disconnected");
    });
    await room.connect(url, token);
    setStatus("joined");
    publishedId = "";
    publishId();
    openCamera(room);
  } catch (err) {
    room = null;
    setStatus(err instanceof Error ? err.message : String(err));
  } finally {
    connecting = false;
  }
}

async function openCamera(joined) {
  for (let i = 0; i < 40; i++) {
    if (!room || room !== joined) return;
    try {
      const video = await createLocalVideoTrack({
        resolution: { width: 640, height: 360, frameRate: 24 },
      });
      localEl = video.attach();
      localEl.autoplay = true;
      localEl.muted = true;
      localEl.playsInline = true;
      localEl.dataset.mirror = "0";
      // Publish your camera so the other person can see you. Keep it off this screen.
      sink.appendChild(localEl);
      localEl.play().catch(() => {});
      await room.localParticipant.publishTrack(video);
      setStatus("camera live");
      return;
    } catch (err) {
      setStatus("camera busy");
      await new Promise((r) => setTimeout(r, 1000));
    }
  }
}

function publishId() {
  if (!room || !state.selfId || publishedId === state.selfId) return;
  const id = state.selfId;
  publishedId = id;
  const me = state.people.get(id);
  room.localParticipant.setAttributes({ tosId: id, tosName: me?.name || "" }).then(() => {
    setStatus("camera live");
  }).catch((err) => {
    publishedId = "";
    setStatus(err instanceof Error ? err.message : String(err));
  });
}

function videoFor(id) {
  if (!id || id === state.selfId) return null;
  for (const row of videos.values()) if (row.tosId === id) return row.el;
  const remotes = [...videos.values()].filter((row) => row.tosId !== state.selfId);
  if (remotes.length === 1) return remotes[0].el;
  return null;
}

function place() {
  const now = performance.now();
  for (const [id, p] of state.people) if (now - p.t > 1500) state.people.delete(id);
  for (const plate of stage.querySelectorAll(".plate")) plate.remove();
  report();
  requestAnimationFrame(place);
}

if (location.hash === "#preview") {
  document.body.classList.add("preview");
  const sample = document.createElement("div");
  sample.className = "sample";
  stage.appendChild(sample);
  const tick = () => {
    const r = sample.getBoundingClientRect();
    sample._pts = platePoints(r.left + r.width / 2, r.top + r.height / 2, r.width / 2, r.height / 2, HEX);
    const wrap = document.createElement("div");
    wrap.className = "plate";
    wrap._pts = sample._pts;
    stage.appendChild(wrap);
    report();
    wrap.remove();
    requestAnimationFrame(tick);
  };
  setStatus("preview");
  requestAnimationFrame(tick);
} else {
  feed();
  requestAnimationFrame(place);
  setTimeout(() => connect(), 300);
  const view = document.createElement("canvas");
  view.style.cssText = "position:absolute;inset:0;width:100%;height:100%";
  stage.appendChild(view);
  const gl = view.getContext("webgl", { alpha: false, antialias: true });
  let drawMesh = () => {};
  if (gl) {
    const compile = (type, src) => {
      const shader = gl.createShader(type);
      gl.shaderSource(shader, src);
      gl.compileShader(shader);
      return shader;
    };
    const prog = gl.createProgram();
    gl.attachShader(prog, compile(gl.VERTEX_SHADER, `
      attribute vec2 aPos;
      attribute vec2 aUv;
      uniform vec2 uRes;
      varying vec2 vUv;
      void main() {
        vec2 c = (aPos / uRes) * 2.0 - 1.0;
        gl_Position = vec4(c.x, -c.y, 0.0, 1.0);
        vUv = aUv;
      }`));
    gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, `
      precision mediump float;
      varying vec2 vUv;
      uniform sampler2D uTex;
      void main() {
        gl_FragColor = texture2D(uTex, vUv);
      }`));
    gl.linkProgram(prog);
    const aPos = gl.getAttribLocation(prog, "aPos");
    const aUv = gl.getAttribLocation(prog, "aUv");
    const uRes = gl.getUniformLocation(prog, "uRes");
    const buf = gl.createBuffer();
    const idx = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, idx);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array([
      0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5,
    ]), gl.STATIC_DRAW);
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.disable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    drawMesh = () => {
      const video = watchedEl;
      const w = Math.max(1, meshW);
      const h = Math.max(1, meshH);
      if (view.width !== w || view.height !== h) {
        view.width = w;
        view.height = h;
      }
      gl.viewport(0, 0, view.width, view.height);
      gl.clearColor(0, 0, 0, 1);
      gl.clear(gl.COLOR_BUFFER_BIT);
      if (!meshXY || !meshUV || !video || video.readyState < 2 || video.videoWidth < 2) return;
      gl.useProgram(prog);
      gl.uniform2f(uRes, view.width, view.height);
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, video);
      const data = new Float32Array(24);
      for (let i = 0; i < 6; i++) {
        data[i * 4] = meshXY[i * 2];
        data[i * 4 + 1] = meshXY[i * 2 + 1];
        data[i * 4 + 2] = meshUV[i * 2];
        data[i * 4 + 3] = meshUV[i * 2 + 1];
      }
      gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW);
      gl.enableVertexAttribArray(aPos);
      gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 16, 0);
      gl.enableVertexAttribArray(aUv);
      gl.vertexAttribPointer(aUv, 2, gl.FLOAT, false, 16, 8);
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, idx);
      gl.drawElements(gl.TRIANGLES, 12, gl.UNSIGNED_SHORT, 0);
    };
  }
  const paint = () => {
    try { drawMesh(); } catch { /* a missed video frame must not stop the card */ }
    requestAnimationFrame(paint);
  };
  requestAnimationFrame(paint);
  const canvas = document.createElement("canvas");
  canvas.width = 320;
  canvas.height = 180;
  const ctx = canvas.getContext("2d");
  const mine = document.createElement("canvas");
  mine.width = 320;
  mine.height = 180;
  const mineCtx = mine.getContext("2d");
  const post = (target, id) => {
    target.toBlob((blob) => {
      if (blob) fetch("/frame?id=" + encodeURIComponent(id), { method: "POST", body: blob });
    }, "image/jpeg", 0.72);
  };
  const dump = () => {
    if (localEl && localEl.readyState >= 2) {
      mineCtx.drawImage(localEl, 0, 0, mine.width, mine.height);
      post(mine, "0");
    }
    for (const [id, p] of state.people) {
      if (p.self || id === state.selfId) continue;
      const vid = videoFor(id);
      if (!vid || vid.readyState < 2) continue;
      ctx.drawImage(vid, 0, 0, canvas.width, canvas.height);
      post(canvas, id);
    }
    setTimeout(dump, 100);
  };
  setTimeout(dump, 800);
  const trackCanvas = document.createElement("canvas");
  trackCanvas.width = 640;
  trackCanvas.height = 360;
  const trackCtx = trackCanvas.getContext("2d");
  let trackBusy = false;
  const sampleFace = () => {
    try {
      const watched = watchedEl;
      if (!trackBusy && trackCtx && watched && watched.readyState >= 2 && watched.videoWidth > 0) {
        const tw = Math.min(640, watched.videoWidth);
        const th = Math.max(1, Math.round(watched.videoHeight * tw / watched.videoWidth));
        if (trackCanvas.width !== tw || trackCanvas.height !== th) {
          trackCanvas.width = tw;
          trackCanvas.height = th;
        }
        trackCtx.drawImage(watched, 0, 0, tw, th);
        trackBusy = true;
        trackCanvas.toBlob((blob) => {
          if (!blob) {
            trackBusy = false;
            return;
          }
          fetch("/track?mir=0", { method: "POST", body: blob }).catch(() => {}).finally(() => {
            trackBusy = false;
          });
        }, "image/jpeg", 0.72);
      }
    } catch {
      trackBusy = false;
    }
    setTimeout(sampleFace, 100);
  };
  setTimeout(sampleFace, 800);
}
