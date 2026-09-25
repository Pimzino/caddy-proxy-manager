/** Joins class names, skipping falsy values. */
export function cn(...parts: Array<string | boolean | null | undefined | number | bigint>): string {
  return parts.filter((p) => typeof p === "string" && p).join(' ');
}
