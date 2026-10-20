import { OneOrMany } from "../core.js";
import { Marker } from "../Marker.js";
import { GlowOptions } from "../styles.js";
import { Layer } from "./Layer.js";
import { Refreshable } from "./Refreshable.js";

// Compiled typings write a mixin's base as a constant; `export {}` keeps it private.
export {};
declare const TileLayerBase: typeof Layer & typeof Refreshable;

/** The module's default export, as many libraries' one-class modules have. It has the `Refreshable` mixin. */
export default class TileLayer extends TileLayerBase {
  constructor(options?: Partial<Pick<TileLayer, "url" | "title" | "visible">>);
  /** The URL template of the tiles. */
  url: string;
  /**
   * The tiles' edge in pixels. The setter also takes one per zoom level, a union with a value-type case (R2), which
   * isn't mapped: the getter is kept, read-only (§5).
   */
  get tileSize(): number;
  set tileSize(value: OneOrMany<number>);
  /** The same tagged intersection as `Marker.glow`. */
  glow?: GlowOptions & { kind: "glow" };
  /** The markers drawn on top of the tiles. */
  readonly emphasized: Marker[];
  /** Highlights one marker or several: `OneOrMany<Marker>` is `Marker | Marker[]` (§2). */
  emphasize(markers: OneOrMany<Marker>): void;
  /** Redeclared with the subclass's type. */
  clone(): TileLayer;
}
