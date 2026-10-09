import { Accessor } from "./core.js";

export type ShapeType = "point" | "line" | "polygon";

/** A data record: plain JSON, no class behind it. */
export interface Bounds {
  xmin: number;
  ymin: number;
  xmax: number;
  ymax: number;
}

export abstract class Shape extends Accessor {
  /** Discriminant: each subclass narrows it to one literal. */
  abstract readonly type: ShapeType;
  readonly extent: Bounds;
  clone(): this;
}

export class Point extends Shape {
  constructor(props?: Partial<Pick<Point, "x" | "y">>);
  readonly type: "point";
  x: number;
  y: number;
}

export class Line extends Shape {
  constructor(props?: Partial<Pick<Line, "path">>);
  readonly type: "line";
  path: [number, number][];
}

export class Polygon extends Shape {
  constructor(props?: Partial<Pick<Polygon, "rings">>);
  readonly type: "polygon";
  rings: [number, number][][];
}
