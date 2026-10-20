import { Observable } from "./core.js";
import { Point } from "./geometry.js";

export class Marker extends Observable {
  static className = "Marker";

  constructor({ position = new Point(), label, style = { type: "icon", name: "pin-1" }, labelFormatter, data } = {}) {
    super();
    this.position = position;
    this.label = label;
    this.style = style;
    this.labelFormatter = labelFormatter;
    this.data = data;
  }

  // The getter always returns a Point; the setter also takes an [x, y] pair.
  get position() { return this._get("position"); }
  set position(value) { this._set("position", Array.isArray(value) ? new Point({ x: value[0], y: value[1] }) : value); }

  get label() { return this._get("label"); }
  set label(value) { this._set("label", value); }
  get style() { return this._get("style"); }
  set style(value) { this._set("style", value); }
  get labelFormatter() { return this._get("labelFormatter"); }
  set labelFormatter(value) { this._set("labelFormatter", value); }

  // Calls the formatter synchronously and uses its return value.
  get displayLabel() {
    return this.labelFormatter ? this.labelFormatter(this) : (this.label ?? "");
  }
}
