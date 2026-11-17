package com.musichoarder.nativeaudio;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertSame;
import static org.junit.Assert.assertTrue;

import java.util.ArrayList;
import java.util.List;
import org.json.JSONObject;
import org.junit.Test;

public class WidgetSnapshotTest {

    private static final String ART = WidgetStore.artName("https://music.example/cover/1");

    private static WidgetSnapshot.Transport transport(boolean playing, long asOf, double position) {
        return new WidgetSnapshot.Transport(playing, position, 201.0, 1.0, asOf, true, true);
    }

    private static WidgetSnapshot.NowPlaying live(boolean playing) {
        return new WidgetSnapshot.NowPlaying("Song", "Artist", "Album", ART, transport(playing, 1000, 12.3));
    }

    @Test
    public void a_live_track_round_trips_through_the_documented_json() throws Exception {
        WidgetSnapshot.NowPlaying parsed = WidgetSnapshot.NowPlaying.fromJson(live(true).toJson());

        assertEquals("Song", parsed.title);
        assertEquals("Artist", parsed.artist);
        assertEquals("Album", parsed.album);
        assertEquals(ART, parsed.art);
        assertEquals(WidgetSnapshot.Reach.BACKGROUND, parsed.reach());
        assertNotNull(parsed.transport);
        assertTrue(parsed.transport.playing);
        assertEquals(12.3, parsed.transport.position, 0);
        assertEquals(201.0, parsed.transport.duration, 0);
        assertEquals(1000L, parsed.transport.asOf);

        JSONObject json = new JSONObject(live(true).toJson());
        assertEquals(1, json.getInt("v"));
        assertEquals("track", json.getString("state"));
        assertEquals("background", json.getString("reach"));
        assertTrue(json.getJSONObject("transport").getBoolean("canNext"));
    }

    @Test
    public void a_demoted_track_keeps_its_words_and_loses_its_transport() throws Exception {
        WidgetSnapshot.NowPlaying demoted = live(true).demoted();

        assertEquals("Song", demoted.title);
        assertEquals(WidgetSnapshot.Reach.NEEDS_APP, demoted.reach());
        assertNull(demoted.transport);
        JSONObject json = new JSONObject(demoted.toJson());
        assertEquals("needsApp", json.getString("reach"));
        assertFalse(json.has("transport"));
        assertSame(demoted, demoted.demoted());
        assertSame(WidgetSnapshot.NowPlaying.NOTHING, WidgetSnapshot.NowPlaying.NOTHING.demoted());
    }

    @Test
    public void a_background_claim_without_a_transport_is_read_as_needing_the_app() {
        String torn = "{\"v\":1,\"state\":\"track\",\"title\":\"Song\",\"artist\":\"A\",\"album\":\"B\",\"art\":null,\"reach\":\"background\"}";

        assertEquals(WidgetSnapshot.Reach.NEEDS_APP, WidgetSnapshot.NowPlaying.fromJson(torn).reach());
    }

    @Test
    public void a_transport_on_a_needs_app_track_is_ignored() {
        String json = live(true).toJson().replace("\"background\"", "\"needsApp\"");

        assertNull(WidgetSnapshot.NowPlaying.fromJson(json).transport);
    }

    @Test
    public void unknown_versions_garbage_and_missing_files_read_as_nothing() {
        assertTrue(WidgetSnapshot.NowPlaying.fromJson(null).isNothing());
        assertTrue(WidgetSnapshot.NowPlaying.fromJson("not json").isNothing());
        assertTrue(WidgetSnapshot.NowPlaying.fromJson("{\"v\":2,\"state\":\"track\",\"title\":\"x\"}").isNothing());
        assertTrue(WidgetSnapshot.NowPlaying.fromJson("{\"v\":1,\"state\":\"track\",\"title\":\"\"}").isNothing());
        assertTrue(WidgetSnapshot.NowPlaying.fromJson("{\"v\":1,\"state\":\"nothing\"}").isNothing());
        assertTrue(WidgetSnapshot.NowPlaying.fromJson(WidgetSnapshot.NowPlaying.NOTHING.toJson()).isNothing());
    }

    @Test
    public void an_art_name_that_is_not_a_cover_file_is_dropped() {
        String json = live(true).toJson().replace(ART, "../../shared_prefs/secrets.xml");

        assertNull(WidgetSnapshot.NowPlaying.fromJson(json).art);
    }

    @Test
    public void equal_content_is_the_same_whatever_the_clock_and_position_say() {
        WidgetSnapshot.NowPlaying a = new WidgetSnapshot.NowPlaying("Song", "Artist", "Album", ART, transport(true, 1000, 1));
        WidgetSnapshot.NowPlaying b = new WidgetSnapshot.NowPlaying("Song", "Artist", "Album", ART, transport(true, 9000, 99));

        assertTrue(a.sameAs(b));
        assertFalse(a.sameAs(new WidgetSnapshot.NowPlaying("Song", "Artist", "Album", ART, transport(false, 1000, 1))));
        assertFalse(a.sameAs(a.demoted()));
        assertFalse(a.sameAs(new WidgetSnapshot.NowPlaying("Song", "Artist", "Album", null, transport(true, 1000, 1))));
        assertTrue(WidgetSnapshot.NowPlaying.NOTHING.sameAs(WidgetSnapshot.NowPlaying.NOTHING));
    }

    @Test
    public void a_nothing_snapshot_carries_nothing_else() {
        WidgetSnapshot.NowPlaying empty = new WidgetSnapshot.NowPlaying("", "Artist", "Album", ART, transport(true, 1, 1));

        assertTrue(empty.isNothing());
        assertNull(empty.transport);
        assertNull(empty.art);
    }

    @Test
    public void the_shelf_round_trips_and_keeps_at_most_eight() {
        List<WidgetSnapshot.ShelfItem> items = new ArrayList<>();
        for (int i = 0; i < 11; i++) items.add(new WidgetSnapshot.ShelfItem("a" + i, "T" + i, "S" + i, i == 0 ? ART : null, "/albums/" + i));

        List<WidgetSnapshot.ShelfItem> parsed = WidgetSnapshot.shelfFromJson(WidgetSnapshot.shelfToJson(items));

        assertEquals(8, parsed.size());
        assertEquals(items.get(0), parsed.get(0));
        assertEquals("/albums/7", parsed.get(7).path);
    }

    @Test
    public void a_shelf_entry_with_a_path_outside_the_app_is_dropped() {
        List<WidgetSnapshot.ShelfItem> items = new ArrayList<>();
        items.add(new WidgetSnapshot.ShelfItem("ok", "T", "S", null, "/album"));
        items.add(new WidgetSnapshot.ShelfItem("host", "T", "S", null, "//evil.example/x"));
        items.add(new WidgetSnapshot.ShelfItem("scheme", "T", "S", null, "https://evil.example/x"));
        items.add(new WidgetSnapshot.ShelfItem("", "T", "S", null, "/album"));

        List<WidgetSnapshot.ShelfItem> parsed = WidgetSnapshot.shelfFromJson(WidgetSnapshot.shelfToJson(items));

        assertEquals(1, parsed.size());
        assertEquals("ok", parsed.get(0).id);
    }

    @Test
    public void a_bad_shelf_file_is_an_empty_shelf() {
        assertTrue(WidgetSnapshot.shelfFromJson(null).isEmpty());
        assertTrue(WidgetSnapshot.shelfFromJson("[").isEmpty());
        assertTrue(WidgetSnapshot.shelfFromJson("{\"v\":9,\"items\":[{\"id\":\"a\",\"path\":\"/x\"}]}").isEmpty());
    }
}
