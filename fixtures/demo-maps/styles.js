// "pin-1" … "pin-120": the literal union with more than 64 members (IconName in styles.d.ts).
export const iconNames = Array.from({ length: 120 }, (_, i) => `pin-${i + 1}`);

const named = { black: [0, 0, 0], white: [255, 255, 255], red: [255, 0, 0], green: [0, 128, 0], blue: [0, 0, 255] };

// Accepts "#rrggbb", a few color names, [r, g, b, a?] or { r, g, b, a? }.
export function toRgba(color) {
  if (typeof color === "string") {
    const hex = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(color);
    const rgb = hex ? hex.slice(1).map((part) => parseInt(part, 16)) : named[color.toLowerCase()];
    if (!rgb) throw new RangeError(`Unknown color: ${color}`);
    return { r: rgb[0], g: rgb[1], b: rgb[2], a: 1 };
  }
  if (Array.isArray(color)) {
    const [r, g, b, a = 1] = color;
    return { r, g, b, a };
  }
  return { a: 1, ...color };
}
