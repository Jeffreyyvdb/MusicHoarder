import { formatBytesShort } from '#lib/formatters.ts';
import type { BackgroundFormat } from './types.ts';

export interface MomentOptions {
  now?: number;
  locale?: string;
  timeZone?: string;
}

const DAY_MS = 86_400_000;

export function formatCount(value: number, locale?: string): string {
  return Math.round(value).toLocaleString(locale);
}

export function formatSeconds(seconds: number): string {
  const total = Math.max(0, Math.round(seconds));
  if (total < 60) return `${total} s`;
  if (total < 3600) {
    const minutes = Math.floor(total / 60);
    const rest = total % 60;
    return minutes < 10 && rest > 0 ? `${minutes} min ${rest} s` : `${minutes} min`;
  }
  if (total < 2 * 86_400) {
    let hours = Math.floor(total / 3600);
    let minutes = Math.round((total % 3600) / 60);
    if (minutes === 60) {
      hours += 1;
      minutes = 0;
    }
    return minutes > 0 ? `${hours} h ${minutes} min` : `${hours} h`;
  }
  let days = Math.floor(total / 86_400);
  let hours = Math.round((total % 86_400) / 3600);
  if (hours === 24) {
    days += 1;
    hours = 0;
  }
  return hours > 0 ? `${days} d ${hours} h` : `${days} d`;
}

export function formatFraction(fraction: number): string {
  const percent = fraction * 100;
  if (percent <= 0) return '0%';
  if (percent < 0.1) return '<0.1%';
  if (percent < 10) return `${percent.toFixed(1)}%`;
  return `${Math.round(percent)}%`;
}

export function formatShare(part: number, whole: number): string {
  if (whole <= 0 || part <= 0) return '0%';
  const percent = (part / whole) * 100;
  if (percent < 1) return '<1%';
  return `${Math.round(percent)}%`;
}

export function formatValue(format: BackgroundFormat, value: number, locale?: string): string {
  switch (format) {
    case 'count':
      return formatCount(value, locale);
    case 'duration':
      return formatSeconds(value);
    case 'bytes':
      return formatBytesShort(value);
    case 'percent':
      return formatFraction(value);
  }
}

function dateParts(
  date: Date,
  options: Intl.DateTimeFormatOptions,
  { locale, timeZone }: MomentOptions
): string {
  return date.toLocaleString(locale, { ...options, timeZone });
}

function dayNumber(date: Date, timeZone: string | undefined, locale: string | undefined): number {
  const parts = new Intl.DateTimeFormat(locale ?? 'en-US', {
    timeZone,
    year: 'numeric',
    month: 'numeric',
    day: 'numeric'
  }).formatToParts(date);
  const pick = (type: string) => Number(parts.find((p) => p.type === type)?.value);
  return Math.round(Date.UTC(pick('year'), pick('month') - 1, pick('day')) / DAY_MS);
}

export function formatMoment(iso: string, options: MomentOptions = {}): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const now = new Date(options.now ?? Date.now());
  const time = dateParts(date, { hour: '2-digit', minute: '2-digit', hour12: false }, options);
  const gap =
    dayNumber(date, options.timeZone, options.locale) -
    dayNumber(now, options.timeZone, options.locale);
  if (gap === 0) return `today ${time}`;
  if (gap === 1) return `tomorrow ${time}`;
  if (gap === -1) return `yesterday ${time}`;
  const sameYear =
    dateParts(date, { year: 'numeric' }, options) === dateParts(now, { year: 'numeric' }, options);
  const day = dateParts(
    date,
    { day: 'numeric', month: 'short', ...(sameYear ? {} : { year: 'numeric' }) },
    options
  );
  return `${day}, ${time}`;
}

export function formatMonthYear(iso: string, options: MomentOptions = {}): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return dateParts(date, { month: 'short', year: 'numeric' }, options);
}

export function formatDayBucket(iso: string, options: MomentOptions = {}): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return dateParts(date, { day: 'numeric', month: 'short' }, options);
}

export function formatHourBucket(iso: string, options: MomentOptions = {}): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const day = dateParts(date, { weekday: 'short' }, options);
  const time = dateParts(date, { hour: '2-digit', minute: '2-digit', hour12: false }, options);
  return `${day} ${time}`;
}

function plural(count: number, unit: string): string {
  return `${count} ${unit}${count === 1 ? '' : 's'}`;
}

export function formatEta(etaUtc: string, options: MomentOptions = {}): string {
  const eta = new Date(etaUtc);
  if (Number.isNaN(eta.getTime())) return '';
  const now = options.now ?? Date.now();
  const gap = eta.getTime() - now;
  if (gap <= 60 * 60 * 1000) return 'within the hour';
  const hours = gap / 3_600_000;
  if (hours < 36) {
    const time = dateParts(eta, { hour: '2-digit', minute: '2-digit', hour12: false }, options);
    return `about ${plural(Math.round(hours), 'hour')}, around ${time}`;
  }
  const days = gap / DAY_MS;
  const sameYear =
    dateParts(eta, { year: 'numeric' }, options) ===
    dateParts(new Date(now), { year: 'numeric' }, options);
  if (days < 60) {
    const day = dateParts(
      eta,
      { day: 'numeric', month: 'short', ...(sameYear ? {} : { year: 'numeric' }) },
      options
    );
    return `about ${plural(Math.round(days), 'day')}, around ${day}`;
  }
  if (days < 730) {
    const month = dateParts(eta, { month: 'short', year: 'numeric' }, options);
    return `about ${plural(Math.round(days / 30.44), 'month')}, around ${month}`;
  }
  return `more than ${Math.floor(days / 365)} years away`;
}
