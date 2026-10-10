import { Observable } from "../core.js";

/** Abstract: only its subclasses are constructed. */
export abstract class Layer extends Observable {
  title: string | null;
  visible: boolean;
  /** A copy, of the same class. */
  clone(): Layer;
}
