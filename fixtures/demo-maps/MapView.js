import { Collection, Evented } from "./core.js";
import { Point, Shape } from "./geometry.js";

export class MapView extends Evented {
  static declaredClass = "views.MapView";
  #markers = new Collection();

  constructor({ center = new Point(), zoom = 1, rotation = 0, basemap = "streets", cursor = "default", title } = {}) {
    super();
    this.center = center;
    this.zoom = zoom;
    this.rotation = rotation;
    this.basemap = basemap;
    this.cursor = cursor;
    this.title = title;
    this.selected = null;
  }

  get center() { return this._get("center"); }
  set center(value) { this._set("center", value); }
  get zoom() { return this._get("zoom"); }
  set zoom(value) { this._set("zoom", Math.min(Math.max(value, 0), 20)); }
  get rotation() { return this._get("rotation"); }
  set rotation(value) { this._set("rotation", ((value % 360) + 360) % 360); }
  get basemap() { return this._get("basemap"); }
  set basemap(value) { this._set("basemap", value); }
  get cursor() { return this._get("cursor"); }
  set cursor(value) { this._set("cursor", value); }
  get title() { return this._get("title"); }
  set title(value) { this._set("title", value); }
  get selected() { return this._get("selected"); }
  set selected(value) { this._set("selected", value); }
  get markers() { return this.#markers; }

  // Screen offset (pixels from the center) to map coordinates; one pixel is 2^-zoom map units.
  toMap(x, y) {
    const scale = 2 ** this.zoom;
    return new Point({ x: this.center.x + x / scale, y: this.center.y - y / scale });
  }

  // Resolves once the view has "moved"; the target is a Shape, an [x, y] pair, Bounds or { center, zoom }.
  async goTo(target, { duration = 0, signal } = {}) {
    signal?.throwIfAborted();
    await new Promise((resolve, reject) => {
      const timer = setTimeout(resolve, duration);
      signal?.addEventListener("abort", () => { clearTimeout(timer); reject(signal.reason); }, { once: true });
    });
    if (target instanceof Shape) {
      const { xmin, ymin, xmax, ymax } = target.extent;
      this.center = new Point({ x: (xmin + xmax) / 2, y: (ymin + ymax) / 2 });
    } else if (Array.isArray(target)) {
      this.center = new Point({ x: target[0], y: target[1] });
    } else if ("center" in target) {
      this.center = new Point({ x: target.center[0], y: target.center[1] });
      this.zoom = target.zoom;
    } else {
      this.center = new Point({ x: (target.xmin + target.xmax) / 2, y: (target.ymin + target.ymax) / 2 });
    }
  }

  // The first marker within 10 pixels of the screen offset, or null.
  async hitTest(x, y) {
    const point = this.toMap(x, y);
    const tolerance = 10 / 2 ** this.zoom;
    return this.#markers.find((m) => Math.hypot(m.position.x - point.x, m.position.y - point.y) <= tolerance) ?? null;
  }
}
