// Misbehaving library code, and code the typings don't have, for edge-case tests (loaded with a module `call`, not part
// of demo-maps).
import { Marker } from "../../fixtures/demo-maps/Marker.js";

export class Source {
  // Returns a subscription whose dispose() throws.
  onFaulty() {
    return { dispose() { throw new Error("dispose failed"); } };
  }

  // Returns an object with neither dispose() nor remove().
  onNothing() {
    return {};
  }

  // Returns itself: an object .NET already holds a handle to.
  onSelf() {
    return this;
  }

  // Counts its live listeners, and returns nothing disposable for "broken".
  listening = 0;
  onCounted(name) {
    if (name === "broken") return {};
    this.listening++;
    return { dispose: () => { this.listening--; } };
  }

  dispose() {}
}

export const source = () => new Source();
export const big = () => 1n; // JSON.stringify can't encode a BigInt
export function throwBare() {
  throw Object.create(null); // no string form
}

// A subclass the typings don't have, as libraries keep internal ones: the metadata doesn't know its runtime class name.
class FastMarker extends Marker {
  static className = "FastMarker";
}
export const fastMarker = () => new FastMarker();

// Another, whose runtime class-name getter throws when it's read on its prototype, as a getter of private state does.
class GuardedMarker extends Marker {
  #name = "GuardedMarker";
  get className() {
    return this.#name;
  }
}
export const guardedMarker = () => new GuardedMarker();

// A proxy whose prototype chain never ends, each prototype a new proxy: the class-id walk gives up.
const endless = () => new Proxy({}, { getPrototypeOf: endless });
export const endlessChain = () => endless();

// A plain class, as an app's own module has: no runtime class name, so its constructor identifies it (R19 step 1).
// demo-maps.meta.mjs registers it; its subclass is one the typings don't have.
export class Plain {}
class InternalPlain extends Plain {}
export const plain = () => new Plain();
export const internalPlain = () => new InternalPlain();

// A subclass the selection leaves out that adds a mixin: the metadata gives it a variant entry (`of`, mapping §5).
class GlowMarker extends Marker {
  static className = "GlowMarker";
}
export const glowMarker = () => new GlowMarker();

// Plain objects, some with methods, as a factory returns (R15).
export const objects = () => ({ data: { x: 1 }, list: [{ y: 2 }, { go() { return 7; } }], tool: { name: "t", go() { return 7; } } });
