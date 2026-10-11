import { MapView } from "@acme/demo-maps/MapView.js";
import { LegendEntry } from "./support/entry.js";

export interface LegendSelectDetail {
  index: number;
  title: string;
}

/**
 * `<demo-legend>`: a view's layers as a list. Attribute: `heading`. Event: `demo-legend-select`.
 * Its class is declared here and reached through `export *` from the package's root, which the manifest names.
 */
export class Legend extends HTMLElement {
  /** The view whose layers it lists. */
  view: MapView;
  heading: string;
  /** One per layer of the view. */
  readonly entries: LegendEntry[];
  /** Selects an entry, as a click on it would. */
  select(index: number): void;
}

/** A legend for a view. */
export function legendFor(view: MapView): Legend;
