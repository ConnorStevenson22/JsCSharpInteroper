// Compiled by `tsc --noEmit` only: checks that the typings resolve the patterns the way callers use them.
import { MapView, type FlyToTarget } from "../MapView.js";
import { Marker } from "../Marker.js";
import { Point, type Shape } from "../geometry.js";
import TileLayer from "../layers/TileLayer.js";
import { centers, describe as describeTarget, distances, perimeter } from "../measure.js";
import { Pin } from "../pins.js";
import * as a from "../ops/a.js";
import * as b from "../ops/b.js";
import type { Style } from "../styles.js";

const view = new MapView({ zoom: 3, basemap: "3d-relief", cursor: "grab" });
view.on("click", (event) => event.mapPoint.x satisfies number);
// @ts-expect-error: not in MapViewEvents
view.on("dblclick", () => {});
view.watch("zoom", (zoom, old) => zoom - old);
view.markers.on("change", ({ added }) => added.map((m) => m.displayLabel));
view.markers.forEach((m, index) => void (m.position.x + index));

const marker = new Marker({ position: [1, 2], labelFormatter: (m) => `${m.position.x}` });
marker.position = [3, 4];
const position: Point = marker.position;
// @ts-expect-error: the getter never returns the setter's tuple form
const pair: [number, number] = marker.position;

const targets: FlyToTarget[] = [position, [0, 0], { minX: 0, minY: 0, maxX: 1, maxY: 1 }, { center: [0, 0], zoom: 2 }];
void Promise.all(targets.map((t) => view.flyTo(t)));

function describe(shape: Shape): string {
  switch (shape.type) {
    case "point": return "point";
    case "line": return "line";
    case "polygon": return "polygon";
  }
}

function width(style: Style): number | undefined {
  return style.type === "stroke" ? style.width : undefined;
}

const tile: TileLayer = new TileLayer({ url: "u" }).clone(); // the redeclared clone returns the subclass
view.layers.add(tile);
perimeter(position, ...targets.filter((t): t is Point => t instanceof Point)) satisfies number;

// A mixin keeps its named base: a Pin is a Marker, with the mixin's members.
const pin = new Pin({ position: [0, 0] });
view.selected = pin;
pin.on("drag", ({ dx, dy }) => dx + dy).remove();
// @ts-expect-error: not in PinEvents
pin.on("click", () => {});
pin.dragBy(1, 2);
pin.meta = { anything: true };
describeTarget(pin) satisfies string;
describeTarget(position) satisfies string;

// Arrays with nullable elements.
distances(position, [marker, null]) satisfies (number | null)[];
centers([position, null, undefined]) satisfies (Point | null | undefined)[];
view.markersAt([[0, 0]]) satisfies (Marker | null)[];

// Sibling modules' Options are different types.
a.execute(1, { scale: 2 });
b.execute(1, { offset: 2 });
// @ts-expect-error: ops/b's Options has no scale
b.execute(1, { scale: 2 });

void pair;
void describe;
void width;

// MarkerEdits.add takes an array or a collection of markers (R2); select takes one, or null.
view.edit({ add: [marker], select: null });
view.edit({ add: view.markers, select: marker });
