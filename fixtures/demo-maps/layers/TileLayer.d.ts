import { Layer } from "./Layer.js";

/** The module's default export, as many libraries' one-class modules have. */
export default class TileLayer extends Layer {
  constructor(options?: Partial<Pick<TileLayer, "url" | "title" | "visible">>);
  /** The URL template of the tiles. */
  url: string;
  /** Redeclared with the subclass's type. */
  clone(): TileLayer;
}
