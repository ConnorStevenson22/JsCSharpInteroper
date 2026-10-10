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
