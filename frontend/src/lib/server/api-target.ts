import { env } from '$env/dynamic/private';

const LOCAL_API_FALLBACK = 'http://localhost:5107';

const API_URL_CANDIDATE_KEYS = [
  'MUSICHOARDER_API_URL',
  'services__musichoarder-api__http__0',
  'services__musichoarder-api__http',
  'SERVICES__MUSICHOARDER-API__HTTP__0',
  'SERVICES__MUSICHOARDER-API__HTTP',
  'ConnectionStrings__musichoarder-api',
  'CONNECTIONSTRINGS__MUSICHOARDER-API'
];

export function getApiBaseUrl(): string {
  for (const key of API_URL_CANDIDATE_KEYS) {
    const value = env[key];
    if (value) return value;
  }

  const discoveredKey = Object.keys(env).find((key) =>
    key.toLowerCase().startsWith('services__musichoarder-api__http')
  );
  if (discoveredKey) {
    const discoveredValue = env[discoveredKey];
    if (discoveredValue) return discoveredValue;
  }

  return LOCAL_API_FALLBACK;
}
