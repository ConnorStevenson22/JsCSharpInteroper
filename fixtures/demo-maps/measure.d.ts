import { Point, Polygon } from "./geometry.js";
import { Marker } from "./Marker.js";

export function distance(a: Point, b: Point): number;

/** The length of the closed path through the points: the last joins the first. */
export function perimeter(...points: Point[]): number;

/** Value-like shapes and an entity: written, never read back (R2, R17). */
export type Measurable = Point | Polygon | Marker;

/** The shape's `type`, or "marker" and its label. */
export function describe(target: Measurable): string;
