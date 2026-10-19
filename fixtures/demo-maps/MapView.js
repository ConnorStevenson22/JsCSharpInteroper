import { Collection, Emitter } from "./core.js";
import { Point, Shape } from "./geometry.js";

export class MapView extends Emitter {
  static className = "MapView";
  #markers = new Collection();
  #layers = new Collection();

  constructor({ center = new Point(), zoom = 1, rotation = 0, basemap = "plain", cursor, title, container = null } = {}) {
    super();
    this.center = center;
    this.zoom = zoom;
    this.rotation = rotation;
    this.basemap = basemap;
    this.cursor = cursor;
    this.title = title;
    this.container = container;
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
  get layers() { return this.#layers; }

  // The element the map draws into, or its id. Only stored, never touched, so the view also runs without a DOM.
  get container() { return this._get("container"); }
  set container(value) { this._set("container", value ?? null); }

  // Screen offset (pixels from the center) to map coordinates, or an [x, y] pair; one pixel is 2^-zoom map units.
  screenToMap(x, y) {
    if (Array.isArray(x)) [x, y] = x;
    const scale = 2 ** this.zoom;
    return new Point({ x: this.center.x + x / scale, y: this.center.y - y / scale });
  }

  // Resolves once the view has "moved"; the target is a Shape, an [x, y] pair, Bounds or { center, zoom }.
  async flyTo(target, { duration = 0, signal } = {}) {
    signal?.throwIfAborted();
    await new Promise((resolve, reject) => {
      const timer = setTimeout(resolve, duration);
      signal?.addEventListener("abort", () => { clearTimeout(timer); reject(signal.reason); }, { once: true });
    });
    if (target instanceof Shape) {
      const { minX, minY, maxX, maxY } = target.bounds;
      this.center = new Point({ x: (minX + maxX) / 2, y: (minY + maxY) / 2 });
    } else if (Array.isArray(target)) {
      this.center = new Point({ x: target[0], y: target[1] });
    } else if ("center" in target) {
      this.center = new Point({ x: target.center[0], y: target.center[1] });
      this.zoom = target.zoom;
    } else {
      this.center = new Point({ x: (target.minX + target.maxX) / 2, y: (target.minY + target.maxY) / 2 });
    }
  }

  // The first marker within 10 pixels of the screen offset, or null.
  async markerAt(x, y) {
    return this.#nearest(x, y);
  }

  markersAt(offsets) {
    return offsets.map(([x, y]) => this.#nearest(x, y));
  }

  #nearest(x, y) {
    const point = this.screenToMap(x, y);
    const tolerance = 10 / 2 ** this.zoom;
    return this.#markers.find((m) => Math.hypot(m.position.x - point.x, m.position.y - point.y) <= tolerance) ?? null;
  }
}
