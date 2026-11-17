package com.musichoarder.nativeaudio;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import org.junit.Test;

public class LaunchActionTest {

    private static final List<WidgetSnapshot.ShelfItem> SHELF = Arrays.asList(
        new WidgetSnapshot.ShelfItem("a1", "Album", "Artist", null, "/albums/one"),
        new WidgetSnapshot.ShelfItem("a2", "Other", "Artist", null, "/albums/two")
    );

    @Test
    public void the_two_fixed_actions_parse_without_a_shelf() {
        assertEquals(LaunchAction.Kind.NOW_PLAYING, LaunchAction.parse("now-playing", Collections.emptyList()).kind);
        assertEquals(LaunchAction.Kind.RESUME, LaunchAction.parse("resume", Collections.emptyList()).kind);
    }

    @Test
    public void a_shelf_id_resolves_to_the_path_the_page_published() {
        LaunchAction action = LaunchAction.parse("shelf/a2", SHELF);

        assertNotNull(action);
        assertEquals(LaunchAction.Kind.OPEN, action.kind);
        assertEquals("/albums/two", action.path);
    }

    @Test
    public void an_unknown_id_or_anything_else_is_dropped() {
        assertNull(LaunchAction.parse("shelf/missing", SHELF));
        assertNull(LaunchAction.parse("shelf/", SHELF));
        assertNull(LaunchAction.parse("shelf/a1/../../x", SHELF));
        assertNull(LaunchAction.parse("https://evil.example", SHELF));
        assertNull(LaunchAction.parse("/albums/one", SHELF));
        assertNull(LaunchAction.parse("", SHELF));
        assertNull(LaunchAction.parse(null, SHELF));
    }

    @Test
    public void the_mailbox_hands_an_action_over_once() {
        LaunchAction.Mailbox mailbox = new LaunchAction.Mailbox();
        mailbox.put(LaunchAction.parse("resume", SHELF), 1_000);

        assertNotNull(mailbox.take(2_000));
        assertNull(mailbox.take(2_000));
    }

    @Test
    public void the_mailbox_keeps_the_latest_action() {
        LaunchAction.Mailbox mailbox = new LaunchAction.Mailbox();
        mailbox.put(LaunchAction.parse("resume", SHELF), 1_000);
        mailbox.put(LaunchAction.parse("shelf/a1", SHELF), 1_500);

        assertEquals("/albums/one", mailbox.take(2_000).path);
    }

    @Test
    public void the_mailbox_drops_an_action_older_than_thirty_seconds() {
        LaunchAction.Mailbox mailbox = new LaunchAction.Mailbox();
        mailbox.put(LaunchAction.parse("resume", SHELF), 1_000);

        assertNull(mailbox.take(1_000 + LaunchAction.MAX_AGE_MS + 1));
        assertNull(mailbox.take(1_000));
    }

    @Test
    public void the_mailbox_still_serves_an_action_at_exactly_thirty_seconds() {
        LaunchAction.Mailbox mailbox = new LaunchAction.Mailbox();
        mailbox.put(LaunchAction.parse("now-playing", SHELF), 1_000);

        assertNotNull(mailbox.take(1_000 + LaunchAction.MAX_AGE_MS));
    }
}
