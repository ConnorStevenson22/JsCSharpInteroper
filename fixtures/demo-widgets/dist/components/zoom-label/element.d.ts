import { MapView } from "@acme/demo-maps/MapView.js";

export interface ZoomLabelCopyDetail {
  text: string;
}

/**
 * `<demo-zoom-label>`: a view's zoom as text. Attribute: `precision`. Event: `demo-zoom-label-copy`.
 * No export reaches this file: the package exports `./components/*` as each folder's `index.js`, which re-exports it,
 * and the manifest names the element's module by that subpath, `components/zoom-label`.
 */
export class ZoomLabelElement extends HTMLElement {
  /** The view whose zoom it shows. */
  view: MapView;
  /** Digits after the decimal point. */
  precision: number;
  /** The zoom, as shown. */
  readonly text: string;
  /** Raises `demo-zoom-label-copy` with the text, as a click on the label would. */
  copy(): void;
}
