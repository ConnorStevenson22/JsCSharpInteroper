// Misbehaving library code for error-path tests (loaded with a module `call`, not part of demo-maps).
export class Source {
  // Returns a subscription whose dispose() throws.
  onFaulty() {
    return { dispose() { throw new Error("dispose failed"); } };
  }

  // Returns itself: an object .NET already holds a handle to.
  onSelf() {
    return this;
  }

  dispose() {}
}

export const source = () => new Source();
export const big = () => 1n; // JSON.stringify can't encode a BigInt
export function throwBare() {
  throw Object.create(null); // no string form
}
