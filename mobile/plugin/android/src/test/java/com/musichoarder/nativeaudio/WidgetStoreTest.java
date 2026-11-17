package com.musichoarder.nativeaudio;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;

import java.io.File;
import java.util.Arrays;
import java.util.Collections;
import org.junit.Before;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;

public class WidgetStoreTest {

    @Rule
    public TemporaryFolder temp = new TemporaryFolder();

    private File root;
    private WidgetStore store;

    @Before
    public void setUp() {
        root = new File(temp.getRoot(), "widgets");
        store = new WidgetStore(root);
    }

    private static WidgetSnapshot.NowPlaying live(String art, boolean playing, long asOf) {
        return new WidgetSnapshot.NowPlaying("Song", "Artist", "Album", art, new WidgetSnapshot.Transport(playing, 1, 200, 1, asOf, true, true));
    }

    @Test
    public void an_empty_store_reads_as_nothing_and_an_empty_shelf() {
        assertTrue(store.nowPlaying().isNothing());
        assertTrue(store.shelf().isEmpty());
    }

    @Test
    public void writing_equal_content_again_writes_nothing() {
        assertTrue(store.replaceNowPlaying(live(null, true, 1)));
        File file = new File(root, "now-playing.json");
        long stamp = file.lastModified();
        file.setLastModified(stamp - 10_000);

        assertFalse(store.replaceNowPlaying(live(null, true, 99)));
        assertEquals(stamp - 10_000, file.lastModified());
        assertTrue(store.replaceNowPlaying(live(null, false, 99)));
    }

    @Test
    public void a_write_leaves_no_temp_file_behind() {
        store.replaceNowPlaying(live(null, true, 1));

        assertEquals(Collections.singletonList("now-playing.json"), Arrays.asList(root.list()));
    }

    @Test
    public void demotion_survives_a_reload() {
        store.replaceNowPlaying(live(null, true, 1));
        store.replaceNowPlaying(store.nowPlaying().demoted());

        assertNull(new WidgetStore(root).nowPlaying().transport);
        assertEquals("Song", new WidgetStore(root).nowPlaying().title);
    }

    @Test
    public void covers_no_document_names_are_pruned_after_a_write() {
        String kept = WidgetStore.artName("https://m/1");
        String shelfArt = WidgetStore.artName("https://m/2");
        String orphan = WidgetStore.artName("https://m/3");
        store.writeArt(kept, new byte[] { 1 });
        store.replaceNowPlaying(live(kept, true, 1));
        store.writeArt(shelfArt, new byte[] { 2 });
        store.writeArt(orphan, new byte[] { 3 });
        store.replaceShelf(Collections.singletonList(new WidgetSnapshot.ShelfItem("a", "T", "S", shelfArt, "/x")));

        assertTrue(store.hasArt(kept));
        assertTrue(store.hasArt(shelfArt));
        assertFalse(store.hasArt(orphan));
    }

    @Test
    public void clear_removes_both_documents_and_every_cover() {
        String art = WidgetStore.artName("https://m/1");
        store.writeArt(art, new byte[] { 1 });
        store.replaceNowPlaying(live(art, true, 1));
        store.replaceShelf(Collections.singletonList(new WidgetSnapshot.ShelfItem("a", "T", "S", art, "/x")));

        store.clear();

        assertTrue(store.nowPlaying().isNothing());
        assertTrue(store.shelf().isEmpty());
        assertFalse(store.hasArt(art));
    }

    @Test
    public void an_art_name_is_the_hash_of_the_url_and_only_that_shape_opens_a_file() {
        assertEquals(WidgetStore.artName("https://m/1"), WidgetStore.artName("https://m/1"));
        assertTrue(WidgetSnapshot.isArtName(WidgetStore.artName("https://m/1")));
        assertNull(store.artFile("../now-playing.json"));
        assertNull(store.artFile(null));
    }
}
