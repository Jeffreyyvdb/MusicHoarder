import { env } from '$env/dynamic/public';

const DEMO_MODE_TRUE_VALUES = new Set(['1', 'true', 'yes', 'on']);

function parseBooleanFlag(value: string | undefined): boolean {
  if (!value) return false;
  return DEMO_MODE_TRUE_VALUES.has(value.trim().toLowerCase());
}

export const isDemoMode = parseBooleanFlag(env.PUBLIC_DEMO_MODE);

export const appDataMode = isDemoMode ? 'demo' : 'production';
