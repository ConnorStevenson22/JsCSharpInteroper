import { Observable } from "./core.js";

// Abstract: only Point, Line and Polygon are constructed.
export class Shape extends Observable {
  static className = "geometry.Shape";

  constructor() {
    super();
    if (new.target === Shape) throw new TypeError("Shape is abstract");
  }

  // A zero box when there are no coordinates.
  get bounds() {
    const coordinates = this._coordinates();
    if (coordinates.length === 0) return { minX: 0, minY: 0, maxX: 0, maxY: 0 };
    const xs = coordinates.map(([x]) => x);
    const ys = coordinates.map(([, y]) => y);
    return { minX: Math.min(...xs), minY: Math.min(...ys), maxX: Math.max(...xs), maxY: Math.max(...ys) };
  }

  clone() {
    return new this.constructor(this);
  }
}

export class Point extends Shape {
  static className = "geometry.Point";

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
  static className = "geometry.Line";

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
  static className = "geometry.Polygon";

  constructor({ vertices = [] } = {}) {
    super();
    this.vertices = vertices;
  }

  get type() { return "polygon"; }
  get vertices() { return this._get("vertices"); }
  set vertices(value) { this._set("vertices", value.map(([x, y]) => [x, y])); }

  _coordinates() {
    return this.vertices;
  }
}
