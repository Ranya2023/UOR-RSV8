package edu.uor.remco

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.database.ContentObserver
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.media.VolumeProvider
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.Settings

/**
 * Keeps Remco alive and listening while the phone is LOCKED, so the volume
 * keys still change slides.
 *
 * Android freezes apps and routes volume keys to "whatever is playing" once the
 * screen is off. So this foreground service:
 *   1. keeps the process (and the Wi-Fi / Bluetooth link) running,
 *   2. plays silence, which makes it the app the volume keys belong to,
 *   3. watches the media volume: up/down → previous/next slide, then puts the
 *      volume straight back so nothing actually gets louder or quieter,
 *   4. also offers a remote media session, which some phones use instead.
 * The phone's normal volume is restored when Remco closes.
 */
class RemoteService : Service() {

    companion object {
        private const val CHANNEL = "remco_remote"
        private const val NOTE_ID = 8

        fun start(ctx: Context) {
            try { ctx.startForegroundService(Intent(ctx, RemoteService::class.java)) } catch (_: Exception) {}
        }
        fun stop(ctx: Context) {
            try { ctx.stopService(Intent(ctx, RemoteService::class.java)) } catch (_: Exception) {}
        }
    }

    private val main = Handler(Looper.getMainLooper())
    private lateinit var am: AudioManager
    private var track: AudioTrack? = null
    private var observer: ContentObserver? = null
    private var session: MediaSession? = null
    private var original = -1
    private var baseline = -1
    private var restoring = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        startInForeground()
        am = getSystemService(Context.AUDIO_SERVICE) as AudioManager
        setup()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_NOT_STICKY

    private fun startInForeground() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "UOR-RC remote", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(
            this, 2, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val note = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle("UOR-RC")
            .setContentText(if (L.ku) "دوگمەکانی دەنگ سلاید دەگۆڕن — تەنانەت کاتێک شاشە داخراوە" else "Volume keys change slides — even with the screen locked")
            .setContentIntent(open)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 29) startForeground(NOTE_ID, note, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        else startForeground(NOTE_ID, note)
    }

    private fun setup() {
        // 1) silence on a loop → this app owns the volume keys when the screen is off
        try {
            val rate = 8000
            val frames = rate / 2
            val t = AudioTrack.Builder()
                .setAudioAttributes(AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build())
                .setAudioFormat(AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(rate)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                .setTransferMode(AudioTrack.MODE_STATIC)
                .setBufferSizeInBytes(frames * 2)
                .build()
            t.write(ShortArray(frames), 0, frames)
            t.setLoopPoints(0, frames, -1)
            t.play()
            track = t
        } catch (_: Exception) {}

        // 2) media volume in the middle, so both "up" and "down" can be noticed
        val max = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        original = am.getStreamVolume(AudioManager.STREAM_MUSIC)
        baseline = (max / 2).coerceAtLeast(1)

        observer = object : ContentObserver(main) {
            override fun onChange(selfChange: Boolean) { onVolumeChanged() }
        }
        contentResolver.registerContentObserver(Settings.System.CONTENT_URI, true, observer!!)

        // 3) remote media session (some phones send the keys here instead)
        try {
            val s = MediaSession(this, "remco-volume")
            s.setPlaybackToRemote(object : VolumeProvider(VOLUME_CONTROL_RELATIVE, 100, 50) {
                override fun onAdjustVolume(direction: Int) {
                    if (direction != 0 && Bus.isConnected?.invoke() == true) main.post { Bus.volume?.invoke(direction > 0) }
                }
            })
            s.setPlaybackState(PlaybackState.Builder().setState(PlaybackState.STATE_PLAYING, 0, 1f).build())
            s.isActive = true
            session = s
        } catch (_: Exception) {}

        Bus.armVolume = { main.post { arm() } }
        if (Bus.isConnected?.invoke() == true) arm()
    }

    /** A PC is connected: put the media volume in the middle so both keys can be noticed. */
    private fun arm() {
        val max = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        baseline = (max / 2).coerceAtLeast(1)
        setVolume(baseline)
    }

    private fun onVolumeChanged() {
        if (restoring) return
        if (Bus.phoneMuted) return                                   // Remco muted the phone itself
        if (System.currentTimeMillis() < Bus.ignoreVolumeUntil) {    // Remco changed the volume itself
            baseline = am.getStreamVolume(AudioManager.STREAM_MUSIC)
            return
        }
        val v = am.getStreamVolume(AudioManager.STREAM_MUSIC)
        if (Bus.isConnected?.invoke() != true || !Bus.volumeKeysWanted) {
            baseline = v   // not connected: leave the user's volume alone
            return
        }
        if (v == baseline) return
        Bus.volume?.invoke(v > baseline)
        setVolume(baseline)
    }

    private fun setVolume(v: Int) {
        restoring = true
        try { am.setStreamVolume(AudioManager.STREAM_MUSIC, v, 0) } catch (_: Exception) {}
        main.postDelayed({ restoring = false }, 150)
    }

    override fun onDestroy() {
        Bus.armVolume = null
        try { observer?.let { contentResolver.unregisterContentObserver(it) } } catch (_: Exception) {}
        try { track?.stop(); track?.release() } catch (_: Exception) {}
        try { session?.isActive = false; session?.release() } catch (_: Exception) {}
        if (original >= 0) try { am.setStreamVolume(AudioManager.STREAM_MUSIC, original, 0) } catch (_: Exception) {}
        super.onDestroy()
    }
}
