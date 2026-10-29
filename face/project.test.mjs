import { cameraBasis, projectHead } from "./public/project.js";

function near(a, b, eps = 1e-6) {
  if (Math.abs(a - b) > eps) throw new Error(`${a} != ${b}`);
}

const id = cameraBasis(0, 0);
near(id.forward[0], 1);
near(id.forward[1], 0);
near(id.right[1], 1);
near(id.up[2], 1);

const yaw90 = cameraBasis(90, 0);
near(yaw90.forward[1], 1, 1e-9);
near(yaw90.right[0], -1, 1e-9);

const view = { w: 1600, h: 900 };
const cam = { x: 0, y: 0, z: 0, yaw: 0, pitch: 0, fov: 90 };
const mid = projectHead(cam, { x: 1000, y: 0, z: 0 }, view);
if (!mid) throw new Error("ahead should project");
near(mid.x, 800, 0.01);
near(mid.y, 450, 0.01);

const right = projectHead(cam, { x: 1000, y: 100, z: 0 }, view);
if (!right || right.x <= mid.x) throw new Error("UE +Y is screen right");

const up = projectHead(cam, { x: 1000, y: 0, z: 100 }, view);
if (!up || up.y >= mid.y) throw new Error("UE +Z is screen up");

if (projectHead(cam, { x: -100, y: 0, z: 0 }, view) !== null) {
  throw new Error("behind the camera should be hidden");
}

console.log("project ok");
