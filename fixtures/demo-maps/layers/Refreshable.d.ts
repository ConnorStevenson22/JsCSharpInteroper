/** A mixin's class, exported: each class that has it implements its interface (§5). */
export abstract class Refreshable {
  constructor(...args: any[]);
  /** Seconds between refreshes; 0 for never. */
  refreshInterval: number;
  /** How many times the layer has refreshed. */
  readonly refreshCount: number;
  /** Fetches the layer's data again. */
  refresh(): void;
  /** A copy, of the same class: `this` is the class's, so the interface leaves it out. */
  sibling(): this;
}
