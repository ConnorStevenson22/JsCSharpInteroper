import { Accessor } from "./core.js";
import { Point } from "./geometry.js";
import { Style } from "./styles.js";

/** Called synchronously; its return value becomes the marker's label. */
export type LabelFormatter = (marker: Marker) => string;

export interface MarkerProperties extends Partial<Pick<Marker, "label" | "style" | "labelFormatter">> {
  position?: Point | [number, number];
}

export class Marker extends Accessor {
  constructor(props?: MarkerProperties);
  get position(): Point;
  set position(value: Point | [number, number]);
  label?: string | null;
  style: Style;
  labelFormatter?: LabelFormatter | null;
  /** `labelFormatter(this)` if set, otherwise `label`, otherwise "". */
  readonly displayLabel: string;
}
