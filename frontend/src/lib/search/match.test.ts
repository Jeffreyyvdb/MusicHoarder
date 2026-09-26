import { describe, expect, it } from 'vitest';
import {
  filterBySearch,
  indexForSearch,
  matchesQuery,
  matchesTerms,
  normalizeFields,
  normalizeForSearch,
  rankBySearch,
  scoreFields,
  searchTerms
} from './match';

/*
 * The cases here are the bugs that motivated the module — a real library's punctuation, a real
 * person's typing. `android/app/src/test/java/com/musichoarder/app/data/SearchMatchTest.kt` mirrors
 * them so the two clients can never disagree about what a query finds.
 */

describe('normalizeForSearch', () => {
  it('folds punctuation, case and diacritics', () => {
    expect(normalizeForSearch('Best Friend (with Fall Out Boy)')).toBe(
      'best friend with fall out boy'
    );
    expect(normalizeForSearch('Beyoncé')).toBe('beyonce');
    expect(normalizeForSearch('  Hip-Hop   /  Rap ')).toBe('hip hop rap');
  });

  it('drops apostrophes instead of splitting on them', () => {
    expect(normalizeForSearch("Girl's Best Friend")).toBe('girls best friend');
    expect(normalizeForSearch('Get Rich Or Die Tryin’')).toBe('get rich or die tryin');
  });

  it('keeps digits and non-Latin letters', () => {
    expect(normalizeForSearch('The Party Never Ends 2.0')).toBe('the party never ends 2 0');
    expect(normalizeForSearch('Пикник')).toBe('пикник');
  });
});

describe('searchTerms', () => {
  it('splits into terms and treats a blank query as no query', () => {
    expect(searchTerms('  best   friend ')).toEqual(['best', 'friend']);
    expect(searchTerms('   ')).toEqual([]);
    expect(searchTerms('!!!')).toEqual([]);
  });
});

describe('matchesTerms', () => {
  const song = normalizeFields(
    'Best Friend (with Fall Out Boy)',
    'Juice WRLD, Fall Out Boy',
    'The Party Never Ends 2.0'
  );

  it('matches across the punctuation the old substring search tripped on', () => {
    // The reported bug: a bracket sat between "friend" and "with".
    expect(matchesTerms(song, searchTerms('best friend with fall out'))).toBe(true);
  });

  it('matches terms spread over different fields, in any order', () => {
    expect(matchesTerms(song, searchTerms('juice best friend'))).toBe(true);
    expect(matchesTerms(song, searchTerms('party best friend'))).toBe(true);
  });

  it('still requires every term', () => {
    expect(matchesTerms(song, searchTerms('best friend drake'))).toBe(false);
  });

  it('matches mid-word fragments', () => {
    expect(matchesTerms(song, searchTerms('wrld'))).toBe(true);
    expect(matchesTerms(song, searchTerms('riend'))).toBe(true);
  });

  it('is true for an empty query', () => {
    expect(matchesTerms(song, [])).toBe(true);
    expect(matchesQuery('', 'anything')).toBe(true);
  });

  it('ignores null and empty fields', () => {
    expect(matchesQuery('unknown', 'Title', null, undefined)).toBe(false);
    expect(matchesQuery('title', 'Title', null)).toBe(true);
  });
});

describe('scoreFields', () => {
  const terms = searchTerms('best friend');
  const score = (title: string, artist: string, album = '') =>
    scoreFields(normalizeFields(title, artist, album), terms);

  it('scores non-matches at zero', () => {
    expect(score('Lucid Dreams', 'Juice WRLD')).toBe(0);
  });

  it('puts an exact title above a title that merely starts with the query', () => {
    expect(score('Best Friend', '50 Cent')).toBeGreaterThan(
      score('Best Friend (with Fall Out Boy)', 'Juice WRLD')
    );
  });

  it('puts a title prefix above a match in the middle of a title', () => {
    expect(score('Best Friend (Remix)', '50 Cent and Olivia')).toBeGreaterThan(
      score("Girl's Best Friend (feat. Ty Dolla $ign)", '2 Chainz')
    );
  });

  it('weights the title over the artist and the album', () => {
    expect(score('Best Friend', 'Someone')).toBeGreaterThan(
      score('Some Song', 'Best Friend', 'Whatever')
    );
  });

  it('scores a phrase hit above the same words scattered', () => {
    expect(score('Best Friend Forever', 'Nobody')).toBeGreaterThan(
      score('Friend Of My Best Days', 'Nobody')
    );
  });
});

describe('rankBySearch', () => {
  type Row = { title: string; artist: string; album: string };
  const rows: Row[] = [
    { title: "Girl's Best Friend (feat. Ty Dolla $ign)", artist: '2 Chainz', album: 'Rap or Go' },
    { title: 'Best Friend', artist: '50 Cent', album: 'Get Rich Or Die Tryin’' },
    { title: 'Best Friend (Remix)', artist: '50 Cent and Olivia', album: 'Best Of 50 Cent' },
    { title: 'Best Friend (feat. Tory Lanez)', artist: 'A Boogie', album: 'International Artist' },
    { title: 'Need a Best Friend', artist: 'A Boogie', album: 'Hoodie SZN' },
    { title: 'Already Best Friends', artist: 'Jack Harlow', album: 'Thats What They All Say' },
    {
      title: 'Best Friend (with Fall Out Boy)',
      artist: 'Juice WRLD, Fall Out Boy',
      album: 'TPNE 2.0'
    }
  ];
  const index = indexForSearch(rows, (r) => [r.title, r.artist, r.album]);

  it('finds the track the old search could not express', () => {
    expect(
      rankBySearch(index, searchTerms('best friend with fall out'), 8).map((r) => r.title)
    ).toEqual(['Best Friend (with Fall Out Boy)']);
  });

  it('ranks by relevance, so a cap keeps the best rather than the first', () => {
    const top = rankBySearch(index, searchTerms('best friend'), 3).map((r) => r.title);
    expect(top[0]).toBe('Best Friend');
    // "Need a Best Friend" and "Girl's Best Friend" match mid-title, so they lose to the prefixes.
    expect(top).not.toContain('Need a Best Friend');
  });

  it('finds a track by artist and title together', () => {
    expect(rankBySearch(index, searchTerms('juice best friend'), 8).map((r) => r.artist)).toEqual([
      'Juice WRLD, Fall Out Boy'
    ]);
  });

  it('returns nothing for an empty query, and respects the cap', () => {
    expect(rankBySearch(index, [], 8)).toEqual([]);
    expect(rankBySearch(index, searchTerms('best'), 2)).toHaveLength(2);
  });

  it('filterBySearch keeps every match, in list order, and passes an empty query through', () => {
    expect(filterBySearch(index, searchTerms('best friend')).map((r) => r.title)).toEqual([
      "Girl's Best Friend (feat. Ty Dolla $ign)",
      'Best Friend',
      'Best Friend (Remix)',
      'Best Friend (feat. Tory Lanez)',
      'Need a Best Friend',
      // Plural: "friend" is found inside "Friends", so the row is a match like any other.
      'Already Best Friends',
      'Best Friend (with Fall Out Boy)'
    ]);
    expect(filterBySearch(index, searchTerms(''))).toHaveLength(rows.length);
  });

  it('is stable for equal scores', () => {
    const dupes = indexForSearch(
      [
        { title: 'Best Friend', artist: '50 Cent', album: 'A' },
        { title: 'Best Friend', artist: '50 Cent', album: 'A' }
      ],
      (r) => [r.title, r.artist, r.album]
    );
    expect(rankBySearch(dupes, searchTerms('best friend'), 8)).toHaveLength(2);
  });
});
