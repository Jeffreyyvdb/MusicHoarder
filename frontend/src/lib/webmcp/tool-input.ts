/**
 * Reading a tool's input. The browser hands `execute` whatever the agent sent; a browser may check
 * it against the input schema, but nothing promises that, so every tool reads its arguments
 * through these and answers a bad one with {@link ToolInputError}.
 *
 * A failure a tool can explain is RETURNED as `{ error }` (see {@link toolFailure}) rather than
 * thrown: a thrown error reaches the agent only as a bare `OperationError`, and the agent needs the
 * sentence to correct its next call.
 */

export class ToolInputError extends Error {}

/** The answer a tool gives when it cannot do what was asked, in words the agent can act on. */
export interface ToolFailure {
  error: string;
}

export function toolFailure(message: string): ToolFailure {
  return { error: message };
}

/** Run a tool body, turning a {@link ToolInputError} or a failed request into a {@link ToolFailure}. */
export async function guarded<T>(body: () => Promise<T> | T): Promise<T | ToolFailure> {
  try {
    return await body();
  } catch (err) {
    if (err instanceof ToolInputError) return toolFailure(err.message);
    const message = err instanceof Error && err.message ? err.message : 'The request failed.';
    return toolFailure(message);
  }
}

export function optionalString(input: Record<string, unknown>, key: string): string | null {
  const value = input[key];
  if (value === undefined || value === null) return null;
  if (typeof value !== 'string') throw new ToolInputError(`"${key}" must be a string.`);
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

export function requiredString(input: Record<string, unknown>, key: string): string {
  const value = optionalString(input, key);
  if (value === null) throw new ToolInputError(`"${key}" is required.`);
  return value;
}

/** An integer id or count. Numeric strings are accepted: agents send `"42"` often enough. */
export function optionalInteger(input: Record<string, unknown>, key: string): number | null {
  const value = input[key];
  if (value === undefined || value === null || value === '') return null;
  const n = typeof value === 'string' ? Number(value) : value;
  if (typeof n !== 'number' || !Number.isInteger(n)) {
    throw new ToolInputError(`"${key}" must be a whole number.`);
  }
  return n;
}

export function requiredInteger(input: Record<string, unknown>, key: string): number {
  const value = optionalInteger(input, key);
  if (value === null) throw new ToolInputError(`"${key}" is required.`);
  return value;
}

export function optionalNumber(input: Record<string, unknown>, key: string): number | null {
  const value = input[key];
  if (value === undefined || value === null || value === '') return null;
  const n = typeof value === 'string' ? Number(value) : value;
  if (typeof n !== 'number' || !Number.isFinite(n)) {
    throw new ToolInputError(`"${key}" must be a number.`);
  }
  return n;
}

export function optionalBoolean(input: Record<string, unknown>, key: string): boolean | null {
  const value = input[key];
  if (value === undefined || value === null) return null;
  if (typeof value !== 'boolean') throw new ToolInputError(`"${key}" must be true or false.`);
  return value;
}

export function requiredBoolean(input: Record<string, unknown>, key: string): boolean {
  const value = optionalBoolean(input, key);
  if (value === null) throw new ToolInputError(`"${key}" is required.`);
  return value;
}

/** A list of integer ids, de-duplicated in order. */
export function integerList(
  input: Record<string, unknown>,
  key: string,
  max: number
): number[] | null {
  const value = input[key];
  if (value === undefined || value === null) return null;
  if (!Array.isArray(value)) throw new ToolInputError(`"${key}" must be a list of ids.`);
  const ids: number[] = [];
  for (const item of value) {
    const n = typeof item === 'string' ? Number(item) : item;
    if (typeof n !== 'number' || !Number.isInteger(n)) {
      throw new ToolInputError(`"${key}" must contain whole numbers only.`);
    }
    if (!ids.includes(n)) ids.push(n);
  }
  if (ids.length > max) throw new ToolInputError(`"${key}" takes at most ${max} ids.`);
  return ids;
}

export function oneOf<const T extends string>(
  input: Record<string, unknown>,
  key: string,
  allowed: readonly T[],
  fallback?: T
): T {
  const value = input[key];
  if ((value === undefined || value === null) && fallback !== undefined) return fallback;
  if (typeof value === 'string' && (allowed as readonly string[]).includes(value)) {
    return value as T;
  }
  throw new ToolInputError(`"${key}" must be one of: ${allowed.join(', ')}.`);
}

/** `value` clamped to [min, max], or `fallback` when absent. */
export function limitOf(
  input: Record<string, unknown>,
  key: string,
  fallback: number,
  max: number
): number {
  const value = optionalInteger(input, key);
  if (value === null) return fallback;
  return Math.min(Math.max(value, 1), max);
}
