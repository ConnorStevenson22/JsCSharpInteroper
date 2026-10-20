import { Observable } from "./core.js";
import { Point } from "./geometry.js";
import { Style } from "./styles.js";

/** Called synchronously; its return value becomes the marker's label. */
export type LabelFormatter = (marker: Marker) => string;

export interface MarkerOptions extends Partial<Pick<Marker, "label" | "style" | "labelFormatter" | "data">> {
  position?: Point | [number, number];
}

export class Marker extends Observable {
  constructor(options?: MarkerOptions);
  /** Where the marker is; a pair is taken as `[x, y]`. */
  get position(): Point;
  set position(value: Point | [number, number]);
  label?: string | null;
  style: Style;
  /** Overrides `label` when set. */
  labelFormatter?: LabelFormatter | null;
  /** The app's own data, untyped; it may hold other markers. */
  data?: any;
  /** `labelFormatter(this)` if set, otherwise `label`, otherwise "". */
  readonly displayLabel: string;
}
