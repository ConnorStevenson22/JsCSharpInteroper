import { Point } from "./geometry.js";

export function distance(a: Point, b: Point): number;

/** The length of the closed path through the points: the last joins the first. */
export function perimeter(...points: Point[]): number;
