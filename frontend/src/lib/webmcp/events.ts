/**
 * Fired on `window` after an agent decides a Tag review item through WebMCP, so a Tag review
 * page that is open drops the row as if the decision had been made on screen.
 */
export const TAG_REVIEW_DECIDED = 'mh:tag-review-decided';

export type TagReviewDecidedDetail = { songId: number };
