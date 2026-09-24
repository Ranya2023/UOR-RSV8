package edu.uor.remco

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Wi-Fi discovery + incoming connection.
 *
 * Remco on the PC broadcasts a UDP beacon every second. When the phone wants a
 * Wi-Fi link to that PC it answers with proof that it knows the PC's PIN; the
 * PC then connects to this phone over TCP. (The PC never has to accept incoming
 * connections, so Windows Firewall never asks anything.)
 */
class WifiSide(context: Context, private val cb: Callback) {

    data class Beacon(
        val id: String, val name: String, val bt: String, val nonce: String,
        val addr: InetAddress, val port: Int, val connected: Boolean, val seen: Long
    )

    interface Callback {
        fun onBeacon(b: Beacon)
        fun onPcConnected(conn: Link.Conn, pcId: String)
    }

    companion object {
        const val BEACON_PORT = 47801
        const val PHONE_PORT = 47802

        fun proof(pin: String, nonce: String): String {
            val h = MessageDigest.getInstance("SHA-256").digest("$pin:$nonce".toByteArray(Charsets.UTF_8))
            return h.joinToString("") { "%02x".format(it) }.take(16)
        }

        /** This phone's IPv4 addresses (for display in settings). */
        fun localAddresses(): List<String> = try {
            NetworkInterface.getNetworkInterfaces().toList()
                .filter { it.isUp && !it.isLoopback }
                .flatMap { it.inetAddresses.toList() }
                .filter { it is java.net.Inet4Address }
                .map { it.hostAddress ?: "" }
        } catch (_: Exception) { emptyList() }
    }

    private val main = Handler(Looper.getMainLooper())
    private val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
    private var mlock: WifiManager.MulticastLock? = null
    @Volatile private var running = false
    private var udp: DatagramSocket? = null
    private var server: ServerSocket? = null
    private val myNonce = ByteArray(6).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }

    /** PC id → PIN that we have answered for recently (only those may connect). */
    private val expected = HashMap<String, String>()

    fun start() {
        if (running) return
        running = true
        try {
            mlock = wifi?.createMulticastLock("remco")?.apply { setReferenceCounted(false); acquire() }
        } catch (_: Exception) {}
        Thread({ udpLoop() }, "wifi-discovery").start()
        Thread({ serverLoop() }, "wifi-server").start()
    }

    /** Re-open the sockets (after joining a new Wi-Fi, so they use it). */
    fun restart() {
        stop()
        Thread({
            try { Thread.sleep(400) } catch (_: InterruptedException) {}
            main.post { start() }
        }, "wifi-restart").start()
    }

    fun stop() {
        running = false
        try { udp?.close() } catch (_: Exception) {}
        try { server?.close() } catch (_: Exception) {}
        try { mlock?.release() } catch (_: Exception) {}
    }

    /** Ask this PC to connect to us over Wi-Fi. */
    fun reply(b: Beacon, pin: String) {
        synchronized(expected) { expected[b.id] = pin }
        val msg = JSONObject()
            .put("remco", 1).put("type", "reply").put("id", b.id)
            .put("phone", Build.MODEL ?: "phone").put("port", PHONE_PORT)
            .put("proof", proof(pin, b.nonce)).put("pnonce", myNonce)
            .toString().toByteArray(Charsets.UTF_8)
        Thread({
            try { udp?.send(DatagramPacket(msg, msg.size, InetSocketAddress(b.addr, b.port))) } catch (_: Exception) {}
        }, "wifi-reply").start()
    }

    private fun udpLoop() {
        while (running) {
            try {
                val s = DatagramSocket(null).apply {
                    reuseAddress = true
                    broadcast = true
                    bind(InetSocketAddress(BEACON_PORT))
                }
                udp = s
                val buf = ByteArray(2048)
                while (running) {
                    val p = DatagramPacket(buf, buf.size)
                    s.receive(p)
                    val o = try { JSONObject(String(p.data, 0, p.length, Charsets.UTF_8)) } catch (_: Exception) { null } ?: continue
                    if (o.optInt("remco") != 1 || o.optString("type") != "beacon") continue
                    val b = Beacon(
                        o.optString("id"), o.optString("name"), o.optString("bt"), o.optString("nonce"),
                        p.address, p.port, o.optBoolean("connected"), System.currentTimeMillis()
                    )
                    main.post { cb.onBeacon(b) }
                }
            } catch (_: Exception) {
                if (running) try { Thread.sleep(1500) } catch (_: InterruptedException) {}
            }
        }
    }

    private fun serverLoop() {
        while (running) {
            try {
                val ss = ServerSocket()
                ss.reuseAddress = true
                ss.bind(InetSocketAddress(PHONE_PORT))
                server = ss
                while (running) {
                    val sock = ss.accept()
                    Thread({ handshake(sock) }, "wifi-handshake").start()
                }
            } catch (_: Exception) {
                if (running) try { Thread.sleep(1500) } catch (_: InterruptedException) {}
            }
        }
    }

    private fun handshake(sock: Socket) {
        try {
            sock.tcpNoDelay = true
            sock.soTimeout = 5000
            val reader = BufferedReader(InputStreamReader(sock.getInputStream(), Charsets.UTF_8))
            val first = JSONObject(reader.readLine() ?: throw Exception("closed"))
            val id = first.optString("id")
            val pin = synchronized(expected) { expected[id] } ?: throw Exception("not expected")
            if (first.optString("e") != "pc_hello" || first.optString("proof") != proof(pin, myNonce)) throw Exception("bad proof")
            sock.soTimeout = 8000 // PC answers our pings; silence = Wi-Fi lost
            val name = first.optString("name").ifBlank { sock.inetAddress.hostAddress ?: "PC" }
            val conn = Link.Conn(reader, sock.getOutputStream(), { try { sock.close() } catch (_: Exception) {} }, name, Link.Kind.WIFI)
            main.post { cb.onPcConnected(conn, id) }
        } catch (_: Exception) {
            try { sock.close() } catch (_: Exception) {}
        }
    }
}
