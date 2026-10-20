import { Collection, Emitter } from "./core.js";
import { Bounds, Point, Shape } from "./geometry.js";
import { Layer } from "./layers/Layer.js";
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
  /** A click on the map, after the view has handled it. */
  click: ClickEvent;
}

export type MapViewOptions = Partial<Pick<MapView, "center" | "zoom" | "rotation" | "basemap" | "cursor" | "title" | "container">>;

export class MapView extends Emitter {
  "$events": MapViewEvents;
  /** A view with the given options; the rest keep their defaults. */
  constructor(options?: MapViewOptions);
  center: Point;
  /** Clamped to 0–20. */
  zoom: number;
  /** Degrees, normalized to 0–360. */
  rotation: number;
  /** @default "plain" */
  basemap: Basemap;
  /** Undefined: the container's own CSS cursor. */
  cursor?: Cursor;
  title?: string | null;
  /** The element the map draws into, or its id. */
  container: HTMLElement | string | null;
  /** The {@link Marker} the user picked last, or `null` when none is. */
  selected: Marker | null;
  readonly markers: Collection<Marker>;
  /** Drawn in order, the first at the bottom. */
  readonly layers: Collection<Layer>;
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
  /**
   * Animates to {@link FlyToTarget a shape, a point or a camera}.
   * Resolves when the view has arrived & settled.
   * @since 0.3
   */
  flyTo(target: FlyToTarget, options?: FlyToOptions): Promise<void>;
  markerAt(x: number, y: number): Promise<Marker | null>;
  /** Back to zoom 1 and rotation 0: the library changes several properties in one call. */
  reset(): void;
}
