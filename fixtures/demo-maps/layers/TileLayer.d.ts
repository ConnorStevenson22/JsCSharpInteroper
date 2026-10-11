import { OneOrMany } from "../core.js";
import { Marker } from "../Marker.js";
import { Layer } from "./Layer.js";

/** The module's default export, as many libraries' one-class modules have. */
export default class TileLayer extends Layer {
  constructor(options?: Partial<Pick<TileLayer, "url" | "title" | "visible">>);
  /** The URL template of the tiles. */
  url: string;
  /**
   * The tiles' edge in pixels. The setter also takes one per zoom level, a union with a value-type case (R2), which
   * isn't mapped: the getter is kept, read-only (§5).
   */
  get tileSize(): number;
  set tileSize(value: OneOrMany<number>);
  /** The markers drawn on top of the tiles. */
  readonly highlighted: Marker[];
  /** Highlights one marker or several: `OneOrMany<Marker>` is `Marker | Marker[]` (§2). */
  highlight(markers: OneOrMany<Marker>): void;
  /** Redeclared with the subclass's type. */
  clone(): TileLayer;
}
