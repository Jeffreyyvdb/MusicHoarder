import { describe, expect, it } from 'vitest';
import { personHref, requestedSection, resolveSettingsView, visibleSections } from './sections';

const admin = { role: 'Owner', isAdmin: true } as const;
const demo = { role: 'Demo', isAdmin: false } as const;
const member = { role: 'Friend', isAdmin: false } as const;

const at = (path: string) => new URL(path, 'http://x');

describe('visibleSections', () => {
  it('gives an admin all six, the demo all but People, a member Account only', () => {
    expect(visibleSections(admin).map((s) => s.id)).toEqual([
      'sources',
      'account',
      'providers',
      'output',
      'people',
      'updates'
    ]);
    expect(visibleSections(demo).map((s) => s.id)).not.toContain('people');
    expect(visibleSections(demo)).toHaveLength(5);
    expect(visibleSections(member).map((s) => s.id)).toEqual(['account']);
  });
});

describe('requestedSection', () => {
  it('reads ?tab=, then a #hash naming a section', () => {
    expect(requestedSection(at('/settings?tab=people'))).toBe('people');
    expect(requestedSection(at('/settings#updates'))).toBe('updates');
    expect(requestedSection(at('/settings?tab=account#updates'))).toBe('account');
    expect(requestedSection(at('/settings#top'))).toBeNull();
    expect(requestedSection(at('/settings'))).toBeNull();
  });
});

describe('resolveSettingsView', () => {
  it('shows the root on a phone and the first section on a desktop for a bare /settings', () => {
    expect(resolveSettingsView(at('/settings'), admin, true)).toEqual({
      section: null,
      person: null,
      redirect: null
    });
    expect(resolveSettingsView(at('/settings'), admin, false)).toEqual({
      section: 'sources',
      person: null,
      redirect: null
    });
    expect(resolveSettingsView(at('/settings'), demo, true).section).toBeNull();
  });

  it('honours the live deep links at both widths', () => {
    for (const tab of ['people', 'sources', 'account', 'updates'] as const) {
      for (const compact of [true, false]) {
        expect(resolveSettingsView(at(`/settings?tab=${tab}`), admin, compact)).toEqual({
          section: tab,
          person: null,
          redirect: null
        });
      }
    }
    expect(resolveSettingsView(at('/settings#updates'), admin, true).section).toBe('updates');
  });

  it('folds an invalid or disallowed section to the root (phone) or the first section (desktop)', () => {
    expect(resolveSettingsView(at('/settings?tab=nope'), admin, true)).toEqual({
      section: null,
      person: null,
      redirect: '/settings'
    });
    expect(resolveSettingsView(at('/settings?tab=nope'), admin, false)).toEqual({
      section: 'sources',
      person: null,
      redirect: '/settings?tab=sources'
    });
    // Not an own key: a prototype name must not read as a retired section.
    expect(resolveSettingsView(at('/settings?tab=constructor'), admin, true).redirect).toBe(
      '/settings'
    );
    // People is administration: the demo is folded away from it, person and all.
    expect(resolveSettingsView(at('/settings?tab=people&person=7'), demo, false)).toEqual({
      section: 'sources',
      person: null,
      redirect: '/settings?tab=sources'
    });
  });

  it('sends the retired Filename rules link to Providers, where its note now lives', () => {
    for (const compact of [true, false]) {
      expect(resolveSettingsView(at('/settings?tab=rules'), admin, compact)).toEqual({
        section: 'providers',
        person: null,
        redirect: '/settings?tab=providers'
      });
    }
    expect(resolveSettingsView(at('/settings?tab=rules'), demo, true).section).toBe('providers');
    expect(resolveSettingsView(at('/settings?tab=rules'), member, true)).toEqual({
      section: 'account',
      person: null,
      redirect: '/settings'
    });
  });

  it('drills into one person under People, and only there', () => {
    const id = 'a1b2 c3';
    expect(personHref(id)).toBe('/settings?tab=people&person=a1b2%20c3');
    for (const compact of [true, false]) {
      expect(resolveSettingsView(at(personHref(id)), admin, compact)).toEqual({
        section: 'people',
        person: id,
        redirect: null
      });
    }
    expect(resolveSettingsView(at('/settings?tab=people&person='), admin, true).person).toBeNull();
    expect(
      resolveSettingsView(at('/settings?tab=account&person=7'), admin, true).person
    ).toBeNull();
  });

  it('shows a member their Account directly, with no root and no URL write', () => {
    for (const compact of [true, false]) {
      expect(resolveSettingsView(at('/settings'), member, compact)).toEqual({
        section: 'account',
        person: null,
        redirect: null
      });
      expect(resolveSettingsView(at('/settings?tab=account'), member, compact)).toEqual({
        section: 'account',
        person: null,
        redirect: null
      });
      expect(resolveSettingsView(at('/settings?tab=people'), member, compact)).toEqual({
        section: 'account',
        person: null,
        redirect: '/settings'
      });
    }
  });
});
