const CHUNK_LOAD_PATTERN =
  /failed to fetch dynamically imported module|error loading dynamically imported module|importing a module script failed|dynamically imported module/i;

export function isChunkLoadError(error: unknown): boolean {
  if (error == null) return false;
  const message =
    typeof error === 'string'
      ? error
      : error instanceof Error
        ? error.message
        : typeof (error as { message?: unknown }).message === 'string'
          ? (error as { message: string }).message
          : '';
  return CHUNK_LOAD_PATTERN.test(message);
}
