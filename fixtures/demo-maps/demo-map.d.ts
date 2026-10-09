import { Point } from "./geometry.js";
import { Basemap, MapView } from "./MapView.js";

export interface DemoMapClickDetail {
  x: number;
  y: number;
  mapX: number;
  mapY: number;
}

/**
 * `<demo-map>`. Attributes: `basemap`, `zoom`. Slots: default, `toolbar`.
 * Events: `demo-map-click`, `demo-map-zoom-change`, `demo-map-ready`.
 */
export class DemoMapElement extends HTMLElement {
  static readonly observedAttributes: string[];
  readonly view: MapView;
  basemap: Basemap;
  zoom: number;
  center: Point;
  /** Clicks the map at an offset from its center, in pixels. */
  clickAt(x: number, y: number, button?: 0 | 1 | 2): void;
}

export interface DemoMapElementEventMap {
  "demo-map-click": CustomEvent<DemoMapClickDetail>;
  "demo-map-zoom-change": CustomEvent<{ zoom: number }>;
  "demo-map-ready": CustomEvent<null>;
}

declare global {
  interface HTMLElementTagNameMap {
    "demo-map": DemoMapElement;
  }
}
