// Module functions. They read fields only, so plain JSON shapes work as well as instances.

export function distance(a, b) {
  return Math.hypot(b.x - a.x, b.y - a.y);
}

export function length(line) {
  let total = 0;
  for (let i = 1; i < line.path.length; i++) {
    const [x1, y1] = line.path[i - 1];
    const [x2, y2] = line.path[i];
    total += Math.hypot(x2 - x1, y2 - y1);
  }
  return total;
}

// Shoelace formula over the outer ring.
export function area(polygon) {
  const ring = polygon.rings[0] ?? [];
  let sum = 0;
  for (let i = 0; i < ring.length; i++) {
    const [x1, y1] = ring[i];
    const [x2, y2] = ring[(i + 1) % ring.length];
    sum += x1 * y2 - x2 * y1;
  }
  return Math.abs(sum) / 2;
}
