package edu.uor.remco

import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * The one active connection to Remco on the PC — Wi-Fi or Bluetooth.
 * Newline-delimited JSON in both directions.
 *
 *  • Bluetooth: this phone connects out and retries until told to stop.
 *  • Wi-Fi: the PC connects IN (see [WifiSide]); [adopt] takes it over and
 *    replaces a Bluetooth link without a gap, so moving from Bluetooth to
 *    Wi-Fi is seamless.
 *
 * All callbacks are delivered on the main thread.
 */
@SuppressLint("MissingPermission") // checked by MainActivity before any Bluetooth call
class Link(private val listener: Listener) {

    enum class Kind { BT, WIFI }

    interface Listener {
        fun onConnecting(name: String, kind: Kind)
        fun onConnected(name: String, kind: Kind)
        fun onDisconnected(kind: Kind, willRetry: Boolean)
        fun onMessage(msg: JSONObject)
    }

    /** An open stream pair. */
    class Conn(
        val reader: BufferedReader,
        val output: OutputStream,
        val close: () -> Unit,
        val name: String,
        val kind: Kind
    )

    companion object {
        /** Must match BtServer.ServiceUuid in Remco.exe. */
        val SERVICE_UUID: UUID = UUID.fromString("7a1c2e90-5b3d-4f6a-9c1e-2d4b8f0a6e31")
    }

    private val main = Handler(Looper.getMainLooper())
    private val lock = Any()
    @Volatile private var generation = 0
    @Volatile private var wantedBt: BluetoothDevice? = null
    @Volatile private var active: Conn? = null
    @Volatile private var queue = LinkedBlockingQueue<String>()

    @Volatile var isConnected = false
        private set
    @Volatile var kind: Kind? = null
        private set
    /** Bluetooth device we are (or will be) using, if any. */
    val bluetoothTarget: BluetoothDevice? get() = wantedBt

    fun connectBt(device: BluetoothDevice) {
        val gen: Int
        synchronized(lock) {
            closeActive()
            wantedBt = device
            gen = ++generation
        }
        Thread({ btLoop(device, gen) }, "bt-link").start()
    }

    /** Take over an incoming Wi-Fi connection (replaces whatever was active). */
    fun adopt(conn: Conn) {
        val gen: Int
        synchronized(lock) {
            wantedBt = null
            closeActive()
            gen = ++generation
        }
        Thread({
            session(conn, gen)
            if (gen == generation) main.post { if (gen == generation) listener.onDisconnected(Kind.WIFI, false) }
        }, "wifi-link").start()
    }

    fun disconnect() {
        synchronized(lock) {
            wantedBt = null
            generation++
            closeActive()
        }
    }

    /** messages waiting to go out (used to drop sound when the link is slow) */
    fun backlog(): Int = queue.size

    fun send(json: JSONObject) {
        if (isConnected) queue.offer(json.toString())
    }

    private fun closeActive() {
        val c = active
        active = null
        isConnected = false
        kind = null
        try { c?.close?.invoke() } catch (_: Exception) {}
    }

    // ── Bluetooth: connect out, retry forever until told otherwise ──────
    private fun btLoop(device: BluetoothDevice, gen: Int) {
        var attempt = 0
        val name = try { device.name } catch (_: SecurityException) { null } ?: device.address
        while (gen == generation && wantedBt == device) {
            if (attempt == 0) main.post { if (gen == generation) listener.onConnecting(name, Kind.BT) }
            val s = openBt(device)
            if (s == null) {
                attempt++
                if (attempt == 1) main.post { if (gen == generation) listener.onDisconnected(Kind.BT, true) }
                sleepQuiet(minOf(attempt, 5) * 1000L)
                continue
            }
            attempt = 0
            val conn = Conn(
                BufferedReader(InputStreamReader(s.inputStream, Charsets.UTF_8)),
                s.outputStream, { try { s.close() } catch (_: Exception) {} }, name, Kind.BT
            )
            session(conn, gen)
            val retry = gen == generation && wantedBt == device
            if (gen == generation) main.post { if (gen == generation) listener.onDisconnected(Kind.BT, retry) }
            if (retry) sleepQuiet(800)
        }
    }

    private fun openBt(device: BluetoothDevice): BluetoothSocket? {
        for (secure in listOf(true, false)) {
            var s: BluetoothSocket? = null
            try {
                s = if (secure) device.createRfcommSocketToServiceRecord(SERVICE_UUID)
                    else device.createInsecureRfcommSocketToServiceRecord(SERVICE_UUID)
                s.connect()
                return s
            } catch (_: Exception) {
                try { s?.close() } catch (_: Exception) {}
            }
        }
        return null
    }

    // ── one live session (both transports) ──────────────────────────────
    private fun session(conn: Conn, gen: Int) {
        synchronized(lock) {
            if (gen != generation) { try { conn.close() } catch (_: Exception) {}; return }
            active = conn
            queue = LinkedBlockingQueue()
            isConnected = true
            kind = conn.kind
        }
        main.post { if (gen == generation) listener.onConnected(conn.name, conn.kind) }

        val q = queue
        val writer = Thread({
            try {
                while (gen == generation) {
                    val line = q.poll(500, TimeUnit.MILLISECONDS) ?: continue
                    conn.output.write((line + "\n").toByteArray(Charsets.UTF_8))
                    conn.output.flush()
                }
            } catch (_: InterruptedException) {
            } catch (_: Exception) {
                try { conn.close() } catch (_: Exception) {}
            }
        }, "link-writer")
        writer.start()

        // heartbeat: lets both sides notice a dead Wi-Fi link within seconds
        val pinger = Thread({
            try {
                while (gen == generation) {
                    Thread.sleep(2000)
                    if (gen == generation) q.offer("{\"c\":\"ping\"}")
                }
            } catch (_: InterruptedException) {}
        }, "link-ping")
        pinger.start()

        q.offer(JSONObject().put("c", "hello").put("v", 2).toString())
        try {
            while (gen == generation) {
                val line = conn.reader.readLine() ?: break
                if (line.isBlank()) continue
                val msg = try { JSONObject(line) } catch (_: Exception) { null } ?: continue
                if (msg.optString("e") == "pong") continue
                main.post { if (gen == generation) listener.onMessage(msg) }
            }
        } catch (_: Exception) {
            // dropped / timed out
        }
        writer.interrupt()
        pinger.interrupt()
        synchronized(lock) {
            if (active === conn) { active = null; isConnected = false; kind = null }
        }
        try { conn.close() } catch (_: Exception) {}
    }

    private fun sleepQuiet(ms: Long) {
        try { Thread.sleep(ms) } catch (_: InterruptedException) {}
    }
}
