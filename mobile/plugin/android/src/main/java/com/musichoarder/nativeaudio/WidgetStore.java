package com.musichoarder.nativeaudio;

import androidx.annotation.Nullable;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * The widgets' two documents and their covers, in one folder:
 *
 *   now-playing.json   written by the engine's publisher
 *   shelf.json         written by the Widgets plugin
 *   art/<sha256 of the cover URL>.jpg
 *
 * Every JSON write is a temp file renamed over the old one, and a cover lands before the JSON
 * that names it, so a reader sees the old document or the new one. Main thread only, which is
 * what keeps "write the cover, then the JSON, then prune" from interleaving with another writer.
 * No cookie, token or server address is ever written here.
 */
final class WidgetStore {

    private static final String NOW_PLAYING = "now-playing.json";
    private static final String SHELF = "shelf.json";
    private static final String ART = "art";

    private final File root;

    WidgetStore(File root) {
        this.root = root;
    }

    WidgetSnapshot.NowPlaying nowPlaying() {
        return WidgetSnapshot.NowPlaying.fromJson(read(new File(root, NOW_PLAYING)));
    }

    List<WidgetSnapshot.ShelfItem> shelf() {
        return WidgetSnapshot.shelfFromJson(read(new File(root, SHELF)));
    }

    /** False when it equals what is stored: nothing was written. */
    boolean replaceNowPlaying(WidgetSnapshot.NowPlaying value) {
        if (value.sameAs(nowPlaying()) && new File(root, NOW_PLAYING).isFile()) return false;
        if (!write(new File(root, NOW_PLAYING), value.toJson())) return false;
        prune();
        return true;
    }

    boolean replaceShelf(List<WidgetSnapshot.ShelfItem> items) {
        String json = WidgetSnapshot.shelfToJson(items);
        if (json.equals(read(new File(root, SHELF)))) return false;
        if (!write(new File(root, SHELF), json)) return false;
        prune();
        return true;
    }

    /** `art/<hex>.jpg` for a cover URL: albums share a file because their tracks share a URL. */
    static String artName(String coverUrl) {
        try {
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(coverUrl.getBytes(StandardCharsets.UTF_8));
            StringBuilder hex = new StringBuilder(ART).append('/');
            for (byte b : digest) hex.append(String.format("%02x", b));
            return hex.append(".jpg").toString();
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e); // every JVM has SHA-256
        }
    }

    @Nullable
    File artFile(@Nullable String name) {
        return name != null && WidgetSnapshot.isArtName(name) ? new File(root, name) : null;
    }

    boolean hasArt(String name) {
        File file = artFile(name);
        return file != null && file.isFile();
    }

    /** Lands the cover; the caller then replaces the JSON that names it. */
    boolean writeArt(String name, byte[] jpeg) {
        File file = artFile(name);
        if (file == null || !ensureDir(file.getParentFile())) return false;
        return writeBytes(file, jpeg);
    }

    /** Another server, or a sign-out: nothing of the old one stays. */
    void clear() {
        File[] covers = new File(root, ART).listFiles();
        if (covers != null) for (File cover : covers) cover.delete();
        new File(root, NOW_PLAYING).delete();
        new File(root, SHELF).delete();
    }

    /** Covers named by neither document are dropped. */
    void prune() {
        File[] covers = new File(root, ART).listFiles();
        if (covers == null) return;
        Set<String> keep = new HashSet<>();
        WidgetSnapshot.NowPlaying now = nowPlaying();
        if (now.art != null) keep.add(now.art);
        for (WidgetSnapshot.ShelfItem item : shelf()) if (item.art != null) keep.add(item.art);
        for (File cover : covers) {
            if (!keep.contains(ART + "/" + cover.getName())) cover.delete();
        }
    }

    @Nullable
    private static String read(File file) {
        if (!file.isFile()) return null;
        try (InputStream in = new FileInputStream(file)) {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            byte[] buffer = new byte[4096];
            int count;
            while ((count = in.read(buffer)) != -1) bytes.write(buffer, 0, count);
            return bytes.toString(StandardCharsets.UTF_8.name());
        } catch (IOException e) {
            return null;
        }
    }

    private boolean write(File file, String text) {
        return ensureDir(root) && writeBytes(file, text.getBytes(StandardCharsets.UTF_8));
    }

    private static boolean writeBytes(File file, byte[] bytes) {
        File temp = new File(file.getParentFile(), file.getName() + ".tmp");
        try (FileOutputStream out = new FileOutputStream(temp)) {
            out.write(bytes);
            out.getFD().sync();
        } catch (IOException e) {
            temp.delete();
            return false;
        }
        if (temp.renameTo(file)) return true;
        temp.delete();
        return false;
    }

    private static boolean ensureDir(File dir) {
        return dir.isDirectory() || dir.mkdirs();
    }
}
