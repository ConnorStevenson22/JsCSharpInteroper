import { Point, Polygon, Shape } from "./geometry.js";
import { Marker } from "./Marker.js";

export type LengthUnit = "meters" | "kilometers";

/** In `unit`s: meters by default, or a unit that many meters long. */
export function distance(a: Point, b: Point, unit?: LengthUnit | number): number;

/** The length of the closed path through the points: the last joins the first. */
export function perimeter(...points: Point[]): number;

/** Value-like shapes and an entity: written, never read back (R2, R17). */
export type Measurable = Point | Polygon | Marker;

/** The shape's `type`, or "marker" and its label. */
export function describe(target: Measurable): string;

/** The distance from `origin` to each marker's position, or `null` for a gap in the list. */
export function distances(origin: Point, markers: (Marker | null)[]): (number | null)[];

/** Each shape's bounds center; `null` and `undefined` elements stay as they are. */
export function centers(shapes: Array<Shape | null | undefined>): (Point | null | undefined)[];
