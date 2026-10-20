import { AnyShape, Line, Point, Polygon, Shape } from "./geometry.js";
import { Marker } from "./Marker.js";

export function distance(a: Point, b: Point): number;

/** The length of the closed path through the points: the last joins the first. */
export function perimeter(...points: Point[]): number;

/** Value-like shapes and an entity: written, never read back (R2, R17). */
export type Measurable = Point | Polygon | Marker;

/** The shape's `type`, or "marker" and its label. */
export function describe(target: Measurable): string;

/** A shape with one vertex as that point; any other shape as it is. The result has no discriminant, only class ids. */
export function simplify(shape: AnyShape): Point | Shape;

/** A shape from its constructor bag tagged with its `type`, the unions nested as typings write them. */
export function shapeFrom(
  options: (((Partial<Pick<Point, "x" | "y">> & { type: "point" }) | (Partial<Pick<Line, "path">> & { type: "line" })) | (Partial<Pick<Polygon, "vertices">> & { type: "polygon" })) | null,
): Shape | null;
