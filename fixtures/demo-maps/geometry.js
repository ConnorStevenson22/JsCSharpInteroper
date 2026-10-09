import { Accessor } from "./core.js";

// Abstract: only Point, Line and Polygon are constructed.
export class Shape extends Accessor {
  static declaredClass = "geometry.Shape";

  constructor() {
    super();
    if (new.target === Shape) throw new TypeError("Shape is abstract");
  }

  get extent() {
    const xs = this._coordinates().map(([x]) => x);
    const ys = this._coordinates().map(([, y]) => y);
    return { xmin: Math.min(...xs), ymin: Math.min(...ys), xmax: Math.max(...xs), ymax: Math.max(...ys) };
  }

  clone() {
    return new this.constructor(this);
  }
}

export class Point extends Shape {
  static declaredClass = "geometry.Point";

  constructor({ x = 0, y = 0 } = {}) {
    super();
    this.x = x;
    this.y = y;
  }

  get type() { return "point"; }
  get x() { return this._get("x"); }
  set x(value) { this._set("x", value); }
  get y() { return this._get("y"); }
  set y(value) { this._set("y", value); }

  _coordinates() {
    return [[this.x, this.y]];
  }
}

export class Line extends Shape {
  static declaredClass = "geometry.Line";

  constructor({ path = [] } = {}) {
    super();
    this.path = path;
  }

  get type() { return "line"; }
  get path() { return this._get("path"); }
  set path(value) { this._set("path", value.map(([x, y]) => [x, y])); }

  _coordinates() {
    return this.path;
  }
}

export class Polygon extends Shape {
  static declaredClass = "geometry.Polygon";

  constructor({ rings = [] } = {}) {
    super();
    this.rings = rings;
  }

  get type() { return "polygon"; }
  get rings() { return this._get("rings"); }
  set rings(value) { this._set("rings", value.map((ring) => ring.map(([x, y]) => [x, y]))); }

  _coordinates() {
    return this.rings.flat();
  }
}
