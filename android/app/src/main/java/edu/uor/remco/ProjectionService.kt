package edu.uor.remco

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.util.Base64
import android.util.DisplayMetrics
import android.view.WindowManager
import org.json.JSONObject
import java.io.ByteArrayOutputStream

/** Tiny bridge between the service and the Activity's connection. */
object Bus {
    @Volatile var send: ((JSONObject) -> Unit)? = null
    @Volatile var backlog: (() -> Int)? = null
    /** live on/off of the phone's sound while the screen is being shown */
    @Volatile var setAudio: ((Boolean) -> Unit)? = null
    @Volatile var audioOn = false
    /** volume key → slide (true = volume up); set by MainActivity */
    @Volatile var volume: ((Boolean) -> Unit)? = null
    @Volatile var isConnected: (() -> Boolean)? = null
    @Volatile var volumeKeysWanted = true
    /** loudness of the sound being sent to the PC (0 … 32767), for the "mute phone" safety check */
    @Volatile var audioLevel = 0
    /** the phone's own speaker is muted by Remco (volume changes are then not slide keys) */
    @Volatile var phoneMuted = false
    /** volume changes Remco makes itself — not slide keys */
    @Volatile var ignoreVolumeUntil = 0L
    /** tells RemoteService to (re)centre the media volume when a PC connects */
    @Volatile var armVolume: (() -> Unit)? = null
    @Volatile var phoneScreenOn = false
    @Volatile var onPhoneScreenChanged: ((Boolean) -> Unit)? = null
    internal var ackListener: (() -> Unit)? = null
    fun ack() { ackListener?.invoke() }
}

/**
 * "Phone screen on the PC": captures this phone's screen (Android asks the
 * user first) and streams it as JPEG frames to Remco on the PC, which shows it
 * full-screen on the projector. Paced by the PC's acknowledgements, so it's as
 * fast as the link allows (smooth on Wi-Fi, slower on Bluetooth).
 */
class ProjectionService : Service() {

    companion object {
        const val EXTRA_CODE = "code"
        const val EXTRA_DATA = "data"
        const val EXTRA_WIDTH = "width"
        const val EXTRA_AUDIO = "audio"
        const val EXTRA_WIFI = "wifi"
        const val ACTION_STOP = "edu.uor.remco.STOP_SCREEN"
        private const val CHANNEL = "remco_screen"
        private const val NOTE_ID = 7
    }

    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var maxWidth = 720
    private var capW = 0; private var capH = 0; private var dpi = 320
    @Volatile private var waiting = false
    @Volatile private var lastSent = 0L
    @Volatile private var pendingFrame = false
    @Volatile private var audioRunning = false
    private var audioThread: Thread? = null
    private var wifi = true

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP || intent == null) { stopSelf(); return START_NOT_STICKY }
        startInForeground()
        val code = intent.getIntExtra(EXTRA_CODE, 0)
        @Suppress("DEPRECATION")
        val data: Intent? = if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(EXTRA_DATA, Intent::class.java)
                            else intent.getParcelableExtra(EXTRA_DATA)
        maxWidth = intent.getIntExtra(EXTRA_WIDTH, 720)
        wifi = intent.getBooleanExtra(EXTRA_WIFI, true)
        val withAudio = intent.getBooleanExtra(EXTRA_AUDIO, false)
        if (data == null) { stopSelf(); return START_NOT_STICKY }
        try {
            val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            val mp = mpm.getMediaProjection(code, data) ?: run { stopSelf(); return START_NOT_STICKY }
            projection = mp
            thread = HandlerThread("remco-capture").also { it.start() }
            handler = Handler(thread!!.looper)
            mp.registerCallback(object : MediaProjection.Callback() {
                override fun onStop() { stopSelf() }
            }, handler)
            Bus.ackListener = { waiting = false; if (pendingFrame) handler?.post { grab() } }
            createDisplay()
            Bus.setAudio = { on -> if (on) startAudio() else stopAudio() }
            if (withAudio) startAudio()
            Bus.phoneScreenOn = true
            Bus.onPhoneScreenChanged?.invoke(true)
            handler?.postDelayed(rotationCheck, 700)
        } catch (e: Exception) {
            stopSelf()
        }
        return START_NOT_STICKY
    }

    private fun startInForeground() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "UOR-RC screen sharing", NotificationManager.IMPORTANCE_LOW))
        val stop = PendingIntent.getService(
            this, 1, Intent(this, ProjectionService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val note = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_slideshow)
            .setContentTitle("UOR-RC")
            .setContentText(if (L.ku) "شاشەی مۆبایل لەسەر کۆمپیوتەر پیشان دەدرێت" else "Showing this phone's screen on the computer")
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, if (L.ku) "وەستاندن" else "Stop", stop).build())
            .build()
        if (Build.VERSION.SDK_INT >= 29) startForeground(NOTE_ID, note, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        else startForeground(NOTE_ID, note)
    }

    @Suppress("DEPRECATION")
    private fun screenSize(): Triple<Int, Int, Int> {
        val m = DisplayMetrics()
        (getSystemService(Context.WINDOW_SERVICE) as WindowManager).defaultDisplay.getRealMetrics(m)
        return Triple(m.widthPixels, m.heightPixels, m.densityDpi)
    }

    private fun createDisplay() {
        val (sw, sh, d) = screenSize()
        val scale = minOf(1f, maxWidth.toFloat() / maxOf(sw, sh))
        capW = (sw * scale).toInt() / 2 * 2
        capH = (sh * scale).toInt() / 2 * 2
        dpi = (d * scale).toInt().coerceAtLeast(80)
        val old = reader
        val r = ImageReader.newInstance(capW, capH, PixelFormat.RGBA_8888, 2)
        r.setOnImageAvailableListener({ pendingFrame = true; if (!waiting) grab() }, handler)
        reader = r
        val vd = display
        if (vd == null) {
            display = projection?.createVirtualDisplay(
                "remco-screen", capW, capH, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, r.surface, null, handler
            )
        } else {
            vd.resize(capW, capH, dpi)
            vd.surface = r.surface
        }
        try { old?.close() } catch (_: Exception) {}
    }

    /** Recreate the capture when the phone turns (portrait ↔ landscape). */
    private val rotationCheck = object : Runnable {
        override fun run() {
            val (sw, sh, _) = screenSize()
            if ((sw > sh) != (capW > capH)) createDisplay()
            handler?.postDelayed(this, 700)
        }
    }

    private fun grab() {
        val r = reader ?: return
        val now = System.currentTimeMillis()
        if (waiting && now - lastSent < 2500) return  // wait for the PC to show the previous frame
        val img = try { r.acquireLatestImage() } catch (_: Exception) { null } ?: return
        pendingFrame = false
        try {
            val plane = img.planes[0]
            val pixelStride = plane.pixelStride
            val rowPadding = plane.rowStride - pixelStride * img.width
            val bmp = Bitmap.createBitmap(img.width + rowPadding / pixelStride, img.height, Bitmap.Config.ARGB_8888)
            bmp.copyPixelsFromBuffer(plane.buffer)
            val crop = if (rowPadding == 0) bmp else Bitmap.createBitmap(bmp, 0, 0, img.width, img.height)
            val out = ByteArrayOutputStream()
            crop.compress(Bitmap.CompressFormat.JPEG, if (maxWidth >= 720) 62 else 48, out)
            if (crop !== bmp) crop.recycle()
            bmp.recycle()
            val send = Bus.send
            if (send != null) {
                waiting = true
                lastSent = now
                send(JSONObject().put("c", "phone_frame").put("img", Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP))
                    .put("w", img.width).put("h", img.height))
            }
        } catch (_: Exception) {
        } finally {
            img.close()
        }
    }

    // ── the phone's sound → PC (Android 10+, apps that allow it) ────────
    private fun startAudio() {
        if (audioRunning || Build.VERSION.SDK_INT < 29) return
        if (checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != android.content.pm.PackageManager.PERMISSION_GRANTED) return
        val mp = projection ?: return
        audioRunning = true
        Bus.audioOn = true
        val rate = if (wifi) 48000 else 16000
        val channels = if (wifi) 2 else 1
        audioThread = Thread({
            var rec: android.media.AudioRecord? = null
            try {
                val cfg = android.media.AudioPlaybackCaptureConfiguration.Builder(mp)
                    .addMatchingUsage(android.media.AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(android.media.AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(android.media.AudioAttributes.USAGE_UNKNOWN)
                    .build()
                val fmt = android.media.AudioFormat.Builder()
                    .setEncoding(android.media.AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(rate)
                    .setChannelMask(if (channels == 2) android.media.AudioFormat.CHANNEL_IN_STEREO else android.media.AudioFormat.CHANNEL_IN_MONO)
                    .build()
                val chunk = rate * channels * 2 * 40 / 1000   // 40 ms
                val min = android.media.AudioRecord.getMinBufferSize(rate,
                    if (channels == 2) android.media.AudioFormat.CHANNEL_IN_STEREO else android.media.AudioFormat.CHANNEL_IN_MONO,
                    android.media.AudioFormat.ENCODING_PCM_16BIT)
                rec = android.media.AudioRecord.Builder()
                    .setAudioFormat(fmt)
                    .setBufferSizeInBytes(maxOf(min, chunk) * 4)
                    .setAudioPlaybackCaptureConfig(cfg)
                    .build()
                rec.startRecording()
                val buf = ByteArray(chunk)
                while (audioRunning) {
                    var got = 0
                    while (got < chunk && audioRunning) {
                        val n = rec.read(buf, got, chunk - got)
                        if (n <= 0) break
                        got += n
                    }
                    if (got <= 0) continue
                    // loudness (peak of this chunk, smoothed) — lets Remco notice if muting the phone also silenced the PC
                    var peak = 0
                    var k = 0
                    while (k + 1 < got) {
                        val v = (buf[k].toInt() and 0xFF) or (buf[k + 1].toInt() shl 8)
                        val a = if (v < 0) -v else v
                        if (a > peak) peak = a
                        k += 8   // every 4th sample is plenty
                    }
                    Bus.audioLevel = (Bus.audioLevel * 3 + peak) / 4
                    // if the link is busy (e.g. Bluetooth), drop sound rather than build up delay
                    if ((Bus.backlog?.invoke() ?: 0) > 25) continue
                    Bus.send?.invoke(JSONObject().put("c", "audio")
                        .put("d", Base64.encodeToString(buf, 0, got, Base64.NO_WRAP)).put("r", rate).put("ch", channels))
                }
            } catch (_: Exception) {
            } finally {
                try { rec?.stop() } catch (_: Exception) {}
                try { rec?.release() } catch (_: Exception) {}
            }
        }, "remco-audio")
        audioThread?.start()
    }

    private fun stopAudio() {
        if (!audioRunning) return
        audioRunning = false
        Bus.audioOn = false
        Bus.send?.invoke(JSONObject().put("c", "audio_stop"))
    }

    override fun onDestroy() {
        stopAudio()
        Bus.setAudio = null
        handler?.removeCallbacks(rotationCheck)
        try { display?.release() } catch (_: Exception) {}
        try { reader?.close() } catch (_: Exception) {}
        try { projection?.stop() } catch (_: Exception) {}
        thread?.quitSafely()
        Bus.ackListener = null
        Bus.phoneScreenOn = false
        Bus.send?.invoke(JSONObject().put("c", "phone_stop"))
        Bus.onPhoneScreenChanged?.invoke(false)
        super.onDestroy()
    }
}
