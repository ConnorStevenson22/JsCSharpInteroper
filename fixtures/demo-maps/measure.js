import { Point } from "./geometry.js";

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

export function distances(origin, markers) {
  return markers.map((marker) => marker && distance(origin, marker.position));
}

export function centers(shapes) {
  return shapes.map((shape) => shape && new Point({ x: (shape.bounds.minX + shape.bounds.maxX) / 2, y: (shape.bounds.minY + shape.bounds.maxY) / 2 }));
}
