import { Layer } from "./Layer.js";

export default class TileLayer extends Layer {
  static className = "layers.TileLayer";

  constructor(options = {}) {
    super(options);
    this.url = options.url ?? "";
  }

  get url() { return this._get("url"); }
  set url(value) { this._set("url", value); }
  // One size, or one per zoom level, of which the getter gives the first.
  get tileSize() { return [this._get("tileSize") ?? 256].flat()[0]; }
  set tileSize(value) { this._set("tileSize", value); }
  get highlighted() { return this._get("highlighted") ?? []; }

  highlight(markers) {
    this._set("highlighted", [markers].flat());
  }
}
