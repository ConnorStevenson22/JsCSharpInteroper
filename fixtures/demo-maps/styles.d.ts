export interface Rgba {
  r: number;
  g: number;
  b: number;
  /**
   * 0–1.
   * @default 1
   */
  a?: number;
}

/** "#rrggbb" or a color name, [r, g, b, a?], or an Rgba object. */
export type ColorInput = string | number[] | Rgba;

export type IconName =
  | "pin-1" | "pin-2" | "pin-3" | "pin-4" | "pin-5" | "pin-6" | "pin-7" | "pin-8" | "pin-9" | "pin-10"
  | "pin-11" | "pin-12" | "pin-13" | "pin-14" | "pin-15" | "pin-16" | "pin-17" | "pin-18" | "pin-19" | "pin-20"
  | "pin-21" | "pin-22" | "pin-23" | "pin-24" | "pin-25" | "pin-26" | "pin-27" | "pin-28" | "pin-29" | "pin-30"
  | "pin-31" | "pin-32" | "pin-33" | "pin-34" | "pin-35" | "pin-36" | "pin-37" | "pin-38" | "pin-39" | "pin-40"
  | "pin-41" | "pin-42" | "pin-43" | "pin-44" | "pin-45" | "pin-46" | "pin-47" | "pin-48" | "pin-49" | "pin-50"
  | "pin-51" | "pin-52" | "pin-53" | "pin-54" | "pin-55" | "pin-56" | "pin-57" | "pin-58" | "pin-59" | "pin-60"
  | "pin-61" | "pin-62" | "pin-63" | "pin-64" | "pin-65" | "pin-66" | "pin-67" | "pin-68" | "pin-69" | "pin-70"
  | "pin-71" | "pin-72" | "pin-73" | "pin-74" | "pin-75" | "pin-76" | "pin-77" | "pin-78" | "pin-79" | "pin-80"
  | "pin-81" | "pin-82" | "pin-83" | "pin-84" | "pin-85" | "pin-86" | "pin-87" | "pin-88" | "pin-89" | "pin-90"
  | "pin-91" | "pin-92" | "pin-93" | "pin-94" | "pin-95" | "pin-96" | "pin-97" | "pin-98" | "pin-99" | "pin-100"
  | "pin-101" | "pin-102" | "pin-103" | "pin-104" | "pin-105" | "pin-106" | "pin-107" | "pin-108" | "pin-109" | "pin-110"
  | "pin-111" | "pin-112" | "pin-113" | "pin-114" | "pin-115" | "pin-116" | "pin-117" | "pin-118" | "pin-119" | "pin-120";

export const iconNames: readonly IconName[];

export interface FillStyle {
  type: "fill";
  color?: ColorInput | null;
  outline?: StrokeStyle | null;
}

export interface StrokeStyle {
  type: "stroke";
  color?: ColorInput | null;
  width?: number;
}

export interface IconStyle {
  type: "icon";
  name: IconName;
  color?: ColorInput | null;
}

/** Discriminated by `type`. */
export type Style = FillStyle | StrokeStyle | IconStyle;

/** Names `StrokeStyle` twice, as generated typings can; still discriminated by `type`. */
export type OutlineStyle = StrokeStyle | IconStyle | StrokeStyle;

export function toRgba(color: ColorInput): Rgba;
