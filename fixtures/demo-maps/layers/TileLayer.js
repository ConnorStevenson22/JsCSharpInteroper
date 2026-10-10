import { Layer } from "./Layer.js";

export default class TileLayer extends Layer {
  static className = "layers.TileLayer";

  constructor(options = {}) {
    super(options);
    this.url = options.url ?? "";
  }

  get url() { return this._get("url"); }
  set url(value) { this._set("url", value); }
}
