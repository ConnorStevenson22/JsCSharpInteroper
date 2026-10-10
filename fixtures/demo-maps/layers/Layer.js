import { Observable } from "../core.js";

// Abstract: only subclasses such as TileLayer are constructed.
export class Layer extends Observable {
  static className = "layers.Layer";

  constructor({ title = null, visible = true } = {}) {
    super();
    if (new.target === Layer) throw new TypeError("Layer is abstract");
    this.title = title;
    this.visible = visible;
  }

  get title() { return this._get("title"); }
  set title(value) { this._set("title", value); }
  get visible() { return this._get("visible"); }
  set visible(value) { this._set("visible", value); }

  clone() {
    return new this.constructor(this);
  }
}
