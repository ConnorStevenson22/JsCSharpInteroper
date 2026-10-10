// No export reaches this file; the package's root re-exports it.

export class Swatch {
  // The runtime class name: the module's path in the package, dotted, plus the class (design §2).
  static className = "dist.support.swatch.Swatch";

  constructor({ color = "black" } = {}) {
    this.color = color;
  }

  get className() {
    return this.constructor.className;
  }
}

export function swatchFor(entry) {
  return new Swatch({ color: entry.visible ? "black" : "grey" });
}
