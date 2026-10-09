import { Collection, Evented } from "./core.js";
import { Bounds, Point, Shape } from "./geometry.js";
import { Marker } from "./Marker.js";

/** "3d-terrain" starts with a digit on purpose (R25). */
export type Basemap = "streets" | "satellite" | "dark-gray" | "3d-terrain";

/** Open-ended: the known values, or any CSS cursor. */
export type Cursor = "default" | "pointer" | "crosshair" | (string & {});

export interface CenterAndZoom {
  center: [number, number];
  zoom: number;
}

/** Untagged: told apart by required members only. */
export type Viewpoint = Bounds | CenterAndZoom;

/** Mixed: a live object, a tuple, and the nested Viewpoint union. */
export type GoToTarget = Shape | [number, number] | Viewpoint;

export interface GoToOptions {
  /** Milliseconds. Defaults to 0. */
  duration?: number;
  signal?: AbortSignal;
}

export interface ClickEvent {
  type: "click";
  /** Pixels from the view's center. */
  x: number;
  y: number;
  button: 0 | 1 | 2;
  mapPoint: Point;
}

export interface MapViewEvents {
  click: ClickEvent;
}

export type MapViewProperties = Partial<Pick<MapView, "center" | "zoom" | "rotation" | "basemap" | "cursor" | "title">>;

export class MapView extends Evented {
  "@eventTypes": MapViewEvents;
  constructor(props?: MapViewProperties);
  center: Point;
  /** Clamped to 0–20. */
  zoom: number;
  /** Degrees, normalized to 0–360. */
  rotation: number;
  basemap: Basemap;
  cursor: Cursor;
  title?: string | null;
  selected: Marker | null;
  readonly markers: Collection<Marker>;
  toMap(x: number, y: number): Point;
  goTo(target: GoToTarget, options?: GoToOptions): Promise<void>;
  hitTest(x: number, y: number): Promise<Marker | null>;
}
