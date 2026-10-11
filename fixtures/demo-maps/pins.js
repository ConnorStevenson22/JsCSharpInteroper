import { Marker } from "./Marker.js";
import { Point } from "./geometry.js";

// A mixin: a function from a base class to a subclass of it.
const Draggable = (Base) => class extends Base {
  #listeners = new Map();

  // Its subscriptions have remove(), not dispose().
  on(type, listener) {
    const listeners = this.#listeners.get(type) ?? new Set();
    this.#listeners.set(type, listeners);
    listeners.add(listener);
    return { remove: () => listeners.delete(listener) };
  }

  dragBy(dx, dy) {
    this.position = new Point({ x: this.position.x + dx, y: this.position.y + dy });
    for (const listener of this.#listeners.get("drag") ?? []) listener({ dx, dy });
  }
};

export class Pin extends Draggable(Marker) {
  static className = "pins.Pin";

  get meta() { return this._get("meta"); }
  set meta(value) { this._set("meta", value); }
}
