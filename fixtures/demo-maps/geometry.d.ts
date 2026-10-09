import { Observable } from "./core.js";

export type ShapeType = "point" | "line" | "polygon";

/** A data record: plain JSON, no class behind it. */
export interface Bounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export abstract class Shape extends Observable {
  /** Discriminant: each subclass narrows it to one literal. */
  abstract readonly type: ShapeType;
  /** All zeros when the shape has no coordinates. */
  readonly bounds: Bounds;
  clone(): this;
}

export class Point extends Shape {
  constructor(options?: Partial<Pick<Point, "x" | "y">>);
  readonly type: "point";
  x: number;
  y: number;
}

export class Line extends Shape {
  constructor(options?: Partial<Pick<Line, "path">>);
  readonly type: "line";
  path: [number, number][];
}

export class Polygon extends Shape {
  constructor(options?: Partial<Pick<Polygon, "vertices">>);
  readonly type: "polygon";
  /** The closing edge from the last vertex back to the first is implied. */
  vertices: [number, number][];
}
