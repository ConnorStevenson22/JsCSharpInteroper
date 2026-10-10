import { Collection, Emitter } from "./core.js";
import { Bounds, Point, Shape } from "./geometry.js";
import { Marker } from "./Marker.js";

/** "3d-relief" starts with a digit on purpose (R25). */
export type Basemap = "plain" | "night" | "relief" | "3d-relief";

/** Open-ended: the known values, or any CSS cursor. */
export type Cursor = "default" | "pointer" | "crosshair" | (string & {});

export interface CenterAndZoom {
  center: [number, number];
  zoom: number;
}

/** Untagged: told apart by required members only. */
export type Camera = Bounds | CenterAndZoom;

/** Mixed: a live object, a tuple, and the nested Camera union. */
export type FlyToTarget = Shape | [number, number] | Camera;

export interface FlyToOptions {
  /**
   * Milliseconds.
   * @default 0
   */
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

export type MapViewOptions = Partial<Pick<MapView, "center" | "zoom" | "rotation" | "basemap" | "cursor" | "title" | "container">>;

export class MapView extends Emitter {
  "$events": MapViewEvents;
  constructor(options?: MapViewOptions);
  center: Point;
  /** Clamped to 0–20. */
  zoom: number;
  /** Degrees, normalized to 0–360. */
  rotation: number;
  basemap: Basemap;
  /** Undefined: the container's own CSS cursor. */
  cursor?: Cursor;
  title?: string | null;
  /** The element the map draws into. */
  container: HTMLElement | null;
  selected: Marker | null;
  readonly markers: Collection<Marker>;
  /**
   * Screen offset (pixels from the center) to map coordinates.
   * @since 0.2
   */
  screenToMap(x: number, y: number): Point;
  /**
   * The offset as an `[x, y]` pair.
   * @deprecated Pass `x` and `y`.
   * @see MapView.flyTo
   */
  screenToMap(offset: [number, number]): Point;
  flyTo(target: FlyToTarget, options?: FlyToOptions): Promise<void>;
  markerAt(x: number, y: number): Promise<Marker | null>;
}
