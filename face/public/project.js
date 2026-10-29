// Unreal camera -> window pixels. UE is X forward, Y right, Z up, yaw 0 looking +X.
// FOV is the horizontal field of view in degrees. view is { w, h } in CSS pixels.
// Returns null when the head is behind the camera.

export function cameraBasis(yawDeg, pitchDeg) {
  const yaw = (yawDeg * Math.PI) / 180;
  const pitch = (pitchDeg * Math.PI) / 180;
  const cy = Math.cos(yaw), sy = Math.sin(yaw);
  const cp = Math.cos(pitch), sp = Math.sin(pitch);
  return {
    forward: [cp * cy, cp * sy, sp],
    right: [-sy, cy, 0],
    up: [-sp * cy, -sp * sy, cp],
  };
}

export function projectHead(cam, head, view) {
  const basis = cameraBasis(cam.yaw, cam.pitch);
  const d = [head.x - cam.x, head.y - cam.y, head.z - cam.z];
  const depth = d[0] * basis.forward[0] + d[1] * basis.forward[1] + d[2] * basis.forward[2];
  if (depth < 30) return null;
  const rx = d[0] * basis.right[0] + d[1] * basis.right[1] + d[2] * basis.right[2];
  const uz = d[0] * basis.up[0] + d[1] * basis.up[1] + d[2] * basis.up[2];
  const fov = cam.fov > 20 && cam.fov < 170 ? cam.fov : 90;
  const tanHalfH = Math.tan(((fov * Math.PI) / 180) / 2);
  const aspect = view.w / view.h;
  const tanHalfV = tanHalfH / aspect;
  const ndcX = rx / (depth * tanHalfH);
  const ndcY = -uz / (depth * tanHalfV);
  return {
    x: (ndcX * 0.5 + 0.5) * view.w,
    y: (ndcY * 0.5 + 0.5) * view.h,
    depth,
    pxPerCm: (view.h / 2) / (depth * tanHalfV),
  };
}

// Six points around (cx, cy). flatFraction 0 is a diamond. 0.72 is the room hex.
export function platePoints(cx, cy, halfW, halfH, flatFraction) {
  const flat = halfW * flatFraction;
  return [
    [cx - flat, cy - halfH],
    [cx + flat, cy - halfH],
    [cx + halfW, cy],
    [cx + flat, cy + halfH],
    [cx - flat, cy + halfH],
    [cx - halfW, cy],
  ];
}
