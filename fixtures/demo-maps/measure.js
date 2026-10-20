import { Line, Point, Polygon } from "./geometry.js";

// Module functions. They read fields only, so plain JSON shapes work as well as instances.

export function distance(a, b) {
  return Math.hypot(b.x - a.x, b.y - a.y);
}

export function perimeter(...points) {
  return points.reduce((sum, point, i) => sum + distance(point, points[(i + 1) % points.length]), 0);
}

export function describe(target) {
  return target.type ?? `marker ${target.displayLabel}`;
}

export function simplify(shape) {
  const [only, ...rest] = shape.type === "line" ? shape.path : shape.type === "polygon" ? shape.vertices : [];
  return only && rest.length === 0 ? new Point({ x: only[0], y: only[1] }) : shape;
}

const shapes = { point: Point, line: Line, polygon: Polygon };

export function shapeFrom(options) {
  if (options == null) return null;
  const { type, ...bag } = options;
  return new shapes[type](bag);
}
