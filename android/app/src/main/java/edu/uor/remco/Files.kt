package edu.uor.remco

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.util.Base64
import android.webkit.MimeTypeMap
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit

/**
 * Files both ways with Remco on the PC, over the same Wi-Fi / Bluetooth link.
 * Received files are saved in  Downloads › Remco.
 * Protocol: phone→PC {"c":"fs",…}, PC→phone {"e":"fs",…}; t = start / chunk / end / ack / done / cancel.
 */
class Files(private val ctx: Context, private val send: (JSONObject) -> Unit, private val ui: Ui) {

    interface Ui {
        fun onTransfer(name: String, percent: Int, toPc: Boolean)
        fun onReceived(name: String, uri: Uri?, mime: String)
        fun onTransferFailed(name: String)
    }

    companion object {
        private const val CHUNK = 48 * 1024
        private const val WINDOW = 8
    }

    private val main = Handler(Looper.getMainLooper())

    // ── sending (phone → PC) ────────────────────────────────────────────
    private data class Item(val uri: Uri, val present: Boolean, val index: Int, val kind: String)
    private val queue = LinkedBlockingQueue<Item>()
    private val acks = ConcurrentHashMap<String, Semaphore>()
    private val cancelled = ConcurrentHashMap<String, Boolean>()
    @Volatile private var worker: Thread? = null

    /** present = show it full-screen on the projector instead of saving it on the PC. */
    /** kind = "doc": a PDF / Word / PowerPoint file the PC should present. */
    fun sendToPc(uris: List<Uri>, present: Boolean = false, kind: String = "") {
        uris.forEachIndexed { i, u -> queue.add(Item(u, present, i, kind)) }
        if (worker?.isAlive != true) {
            worker = Thread({
                while (true) {
                    val it = queue.poll(1, TimeUnit.SECONDS) ?: break
                    sendOne(it.uri, it.present, it.index, it.kind)
                }
            }, "remco-files").also { it.start() }
        }
    }

    private fun nameAndSize(u: Uri): Pair<String, Long> {
        var name = u.lastPathSegment ?: "file"
        var size = -1L
        try {
            ctx.contentResolver.query(u, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    val ni = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val si = c.getColumnIndex(OpenableColumns.SIZE)
                    if (ni >= 0 && !c.isNull(ni)) name = c.getString(ni)
                    if (si >= 0 && !c.isNull(si)) size = c.getLong(si)
                }
            }
        } catch (_: Exception) {}
        return name to size
    }

    /** total bytes of these files (to warn before a big video goes over Bluetooth) */
    fun totalSize(uris: List<Uri>): Long = uris.sumOf { maxOf(0L, nameAndSize(it).second) }

    private fun sendOne(u: Uri, present: Boolean, index: Int, kind: String) {
        val (name, size) = nameAndSize(u)
        val id = UUID.randomUUID().toString().replace("-", "").take(10)
        val sem = Semaphore(0)
        acks[id] = sem
        try {
            send(JSONObject().put("c", "fs").put("t", "start").put("id", id).put("name", name).put("size", size)
                .put("present", present).put("index", index).put("kind", kind))
            if (!sem.tryAcquire(15, TimeUnit.SECONDS) || cancelled.containsKey(id)) { fail(name); return }
            sem.release(WINDOW)
            ctx.contentResolver.openInputStream(u)?.use { input ->
                val buf = ByteArray(CHUNK)
                var sent = 0L; var seq = 0; var lastPct = -1
                while (true) {
                    val n = input.read(buf)
                    if (n <= 0) break
                    if (!sem.tryAcquire(20, TimeUnit.SECONDS) || cancelled.containsKey(id)) {
                        send(JSONObject().put("c", "fs").put("t", "cancel").put("id", id)); fail(name); return
                    }
                    send(JSONObject().put("c", "fs").put("t", "chunk").put("id", id).put("seq", seq++)
                        .put("d", Base64.encodeToString(buf, 0, n, Base64.NO_WRAP)))
                    sent += n
                    val pct = if (size > 0) (sent * 100 / size).toInt() else 50
                    if (pct != lastPct) { lastPct = pct; main.post { ui.onTransfer(name, pct, true) } }
                }
            } ?: run { fail(name); return }
            send(JSONObject().put("c", "fs").put("t", "end").put("id", id))
            main.post { ui.onTransfer(name, 100, true) }
        } catch (_: Exception) {
            fail(name)
        } finally {
            acks.remove(id); cancelled.remove(id)
        }
    }

    private fun fail(name: String) = main.post { ui.onTransferFailed(name) }

    // ── receiving (PC → phone) ─────────────────────────────────────────
    private class Incoming(val out: OutputStream, val uri: Uri?, val file: File?, val name: String, val mime: String, val size: Long) {
        var got = 0L
    }
    private val incoming = HashMap<String, Incoming>()

    /** An {"e":"fs",…} message from the PC (called on the main thread). */
    fun handle(m: JSONObject) {
        val id = m.optString("id")
        when (m.optString("t")) {
            "ack" -> acks[id]?.release()
            "cancel" -> {
                cancelled[id] = true
                acks[id]?.release(WINDOW + 1)
                incoming.remove(id)?.let { abort(it) }
            }
            "start" -> {
                val name = m.optString("name").ifBlank { "file" }.replace("/", "_")
                val ext = name.substringAfterLast('.', "").lowercase()
                val mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"
                try {
                    val inc = create(name, mime, m.optLong("size", -1))
                    incoming[id] = inc
                    ui.onTransfer(name, 0, false)
                    send(JSONObject().put("c", "fs").put("t", "ack").put("id", id).put("seq", -1))
                } catch (_: Exception) {
                    send(JSONObject().put("c", "fs").put("t", "cancel").put("id", id))
                    ui.onTransferFailed(name)
                }
            }
            "chunk" -> {
                val inc = incoming[id] ?: return
                try {
                    val data = Base64.decode(m.optString("d"), Base64.DEFAULT)
                    inc.out.write(data)
                    inc.got += data.size
                    if (inc.size > 0) ui.onTransfer(inc.name, (inc.got * 100 / inc.size).toInt(), false)
                    send(JSONObject().put("c", "fs").put("t", "ack").put("id", id).put("seq", m.optInt("seq")))
                } catch (_: Exception) {
                    incoming.remove(id); abort(inc)
                    send(JSONObject().put("c", "fs").put("t", "cancel").put("id", id))
                    ui.onTransferFailed(inc.name)
                }
            }
            "end" -> {
                val inc = incoming.remove(id) ?: return
                try {
                    inc.out.close()
                    if (Build.VERSION.SDK_INT >= 29 && inc.uri != null) {
                        ctx.contentResolver.update(inc.uri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
                    }
                    send(JSONObject().put("c", "fs").put("t", "done").put("id", id).put("ok", true))
                    ui.onTransfer(inc.name, 100, false)
                    ui.onReceived(inc.name, inc.uri, inc.mime)
                } catch (_: Exception) { ui.onTransferFailed(inc.name) }
            }
        }
    }

    private fun create(name: String, mime: String, size: Long): Incoming {
        return if (Build.VERSION.SDK_INT >= 29) {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, name)
                put(MediaStore.Downloads.MIME_TYPE, mime)
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/UOR-RC")
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val uri = ctx.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw Exception("no uri")
            val out = ctx.contentResolver.openOutputStream(uri) ?: throw Exception("no stream")
            Incoming(out, uri, null, name, mime, size)
        } else {
            // Android 8–9: app's own Downloads folder (no storage permission needed)
            val dir = File(ctx.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "UOR-RC").apply { mkdirs() }
            var f = File(dir, name)
            var i = 2
            while (f.exists()) { f = File(dir, name.substringBeforeLast('.') + " ($i)." + name.substringAfterLast('.', "bin")); i++ }
            Incoming(FileOutputStream(f), null, f, name, mime, size)
        }
    }

    private fun abort(inc: Incoming) {
        try { inc.out.close() } catch (_: Exception) {}
        try { if (inc.uri != null) ctx.contentResolver.delete(inc.uri, null, null) } catch (_: Exception) {}
        try { inc.file?.delete() } catch (_: Exception) {}
    }

    fun cancelAll() {
        for ((_, inc) in incoming) abort(inc)
        incoming.clear()
        for ((id, s) in acks) { cancelled[id] = true; s.release(WINDOW + 1) }
        queue.clear()
    }
}
