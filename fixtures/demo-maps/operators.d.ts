import { Line, Point, Polygon } from "./geometry.js";

export function distance(a: Point, b: Point): number;
export function length(line: Line): number;
/** Area of the outer ring. */
export function area(polygon: Polygon): number;
