package edu.uor.remco

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.ImageFormat
import android.graphics.Matrix
import android.graphics.Rect
import android.graphics.YuvImage
import android.media.ImageReader
import android.graphics.RectF
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.util.Base64
import android.util.Size
import android.view.Gravity
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors
import kotlin.math.abs
import kotlin.math.max

/**
 * 📷 Document camera: the phone's back camera, live and full-screen on the projector.
 * Hold the phone over a paper, a book or an object. Pinch to zoom, tap to focus,
 * ⚡ light, ⏸ freeze the picture (so you can move the phone away), 🔄 rotate on the PC.
 * Frames go to the PC exactly like "Phone screen on PC", so laser / pen / zoom work on top.
 */
class CameraActivity : Activity() {

    companion object {
        const val EXTRA_WIDTH = "width"
    }

    private lateinit var texture: AutoFitTextureView
    private lateinit var status: TextView
    private val main = Handler(Looper.getMainLooper())
    private var bgThread: HandlerThread? = null
    private var bg: Handler? = null
    private val encoder = Executors.newSingleThreadExecutor()

    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var request: CaptureRequest.Builder? = null
    private var reader: ImageReader? = null
    private var autoRot = 0
    private var chars: CameraCharacteristics? = null
    private var previewSize = Size(1280, 720)
    private var frameSize = Size(1280, 720)
    private var sensorRect: Rect? = null
    private var maxZoom = 1f
    private var zoom = 1f
    private var torch = false
    private var frozen = false
    private var outWidth = 1280

    @Volatile private var waiting = false
    @Volatile private var encoding = false
    @Volatile private var lastSent = 0L
    @Volatile private var shotWanted = false
    @Volatile private var micOn = false
    private var micThread: Thread? = null
    private var rot = 0
    private var fill = false

    private fun dp(v: Float) = (v * resources.displayMetrics.density).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_FULLSCREEN)
        outWidth = intent.getIntExtra(EXTRA_WIDTH, 1280)
        rot = getSharedPreferences("remco", Context.MODE_PRIVATE).getInt("camRot", 0)

        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        texture = AutoFitTextureView(this)
        root.addView(texture, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT, Gravity.CENTER))

        status = TextView(this).apply {
            setTextColor(Color.WHITE); textSize = 13f
            setBackgroundColor(Color.parseColor("#80000000")); setPadding(dp(10f), dp(4f), dp(10f), dp(4f))
            text = if (L.ku) "📷 کامێرا · پیشان دەدرێت لەسەر پرۆجێکتەر" else "📷 Document camera · live on the projector"
        }
        root.addView(status, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply { topMargin = dp(10f) })

        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER
            setBackgroundColor(Color.parseColor("#99000000")); setPadding(dp(8f), dp(6f), dp(8f), dp(6f))
        }
        fun btn(t: String, onClick: (Button) -> Unit) = Button(this).apply {
            text = t; isAllCaps = false; textSize = 15f; setTextColor(Color.WHITE)
            setBackgroundResource(R.drawable.btn_tool); stateListAnimator = null
            minWidth = 0; minHeight = 0; minimumWidth = 0; minimumHeight = 0
            setPadding(dp(14f), 0, dp(14f), 0)
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44f)).apply { marginEnd = dp(8f) }
            setOnClickListener { onClick(this) }
        }
        bar.addView(btn(if (L.ku) "⚡ ڕووناکی" else "⚡ Light") { b -> torch = !torch; b.isSelected = torch; updateRequest() })
        bar.addView(btn(if (L.ku) "⏸ ڕاگرتن" else "⏸ Freeze") { b ->
            frozen = !frozen; b.isSelected = frozen
            b.text = if (frozen) (if (L.ku) "▶ ڕاستەوخۆ" else "▶ Live") else (if (L.ku) "⏸ ڕاگرتن" else "⏸ Freeze")
        })
        bar.addView(btn("🔍−") { setZoom(zoom / 1.4f) })
        bar.addView(btn("🔍+") { setZoom(zoom * 1.4f) })
        bar.addView(btn("📸") { shoot() })
        bar.addView(btn("🖼") { Bus.send?.invoke(JSONObject().put("c", "gallery").put("a", "show")) })
        bar.addView(btn(if (L.ku) "🎤 مایک" else "🎤 Mic") { b -> toggleMic(b) })
        bar.addView(btn("🔄") { rot = (rot + 90) % 360; sendView() })
        bar.addView(btn("⛶") { b -> fill = !fill; b.isSelected = fill; sendView() })
        bar.addView(btn("✕") { finish() })
        root.addView(bar, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(12f) })
        setContentView(root)

        // pinch = zoom, tap = focus
        val scale = ScaleGestureDetector(this, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(d: ScaleGestureDetector): Boolean { setZoom(zoom * d.scaleFactor); return true }
        })
        texture.setOnTouchListener { _, e ->
            scale.onTouchEvent(e)
            if (e.actionMasked == MotionEvent.ACTION_UP && !scale.isInProgress) focus()
            true
        }

        Bus.ackListener = { waiting = false }
        sendView()
    }

    override fun onResume() {
        super.onResume()
        bgThread = HandlerThread("remco-camera").also { it.start() }
        bg = Handler(bgThread!!.looper)
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), 1)
            return
        }
        startWhenReady()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 2) return    // microphone: tap 🎤 again once it is allowed
        if (grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED) startWhenReady() else finish()
    }

    private fun startWhenReady() {
        if (texture.isAvailable) openCamera(texture.width, texture.height)
        else texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(st: SurfaceTexture, w: Int, h: Int) = openCamera(w, h)
            override fun onSurfaceTextureSizeChanged(st: SurfaceTexture, w: Int, h: Int) = configureTransform(w, h)
            override fun onSurfaceTextureDestroyed(st: SurfaceTexture) = true
            override fun onSurfaceTextureUpdated(st: SurfaceTexture) {}
        }
        main.removeCallbacks(frameTick)
        main.postDelayed(frameTick, 300)
    }

    override fun onPause() {
        main.removeCallbacks(frameTick)
        closeCamera()
        bgThread?.quitSafely(); bgThread = null; bg = null
        super.onPause()
    }

    override fun onDestroy() {
        micOn = false
        Bus.send?.invoke(JSONObject().put("c", "audio_stop"))
        Bus.ackListener = null
        Bus.send?.invoke(JSONObject().put("c", "phone_stop"))
        encoder.shutdown()
        super.onDestroy()
    }

    // ── camera ─────────────────────────────────────────────────────────
    @SuppressLint("MissingPermission")
    private fun openCamera(viewW: Int, viewH: Int) {
        val cm = getSystemService(Context.CAMERA_SERVICE) as CameraManager
        try {
            val id = cm.cameraIdList.firstOrNull {
                cm.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK
            } ?: cm.cameraIdList.firstOrNull() ?: run { finish(); return }
            val c = cm.getCameraCharacteristics(id)
            chars = c
            sensorRect = c.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE)
            maxZoom = max(1f, c.get(CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM) ?: 1f)
            val map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return
            val sizes = map.getOutputSizes(SurfaceTexture::class.java) ?: arrayOf(Size(1280, 720))
            // 16:9, as close to 1280×720 as possible (good for projectors), else the closest size
            previewSize = sizes.filter { abs(it.width * 9 - it.height * 16) < 32 && it.width <= 1920 }
                .minByOrNull { abs(it.width - 1280) }
                ?: sizes.minByOrNull { abs(it.width - 1280) } ?: Size(1280, 720)
            val yuv = map.getOutputSizes(ImageFormat.YUV_420_888) ?: arrayOf(previewSize)
            frameSize = yuv.filter { abs(it.width * 9 - it.height * 16) < 32 }
                .minByOrNull { abs(it.width - outWidth) }
                ?: yuv.minByOrNull { abs(it.width - outWidth) } ?: previewSize
            // the camera chip is usually mounted sideways: work out the turn needed for an upright picture
            val sensor = c.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
            @Suppress("DEPRECATION")
            val disp = when (windowManager.defaultDisplay.rotation) {
                Surface.ROTATION_90 -> 90; Surface.ROTATION_180 -> 180; Surface.ROTATION_270 -> 270; else -> 0
            }
            autoRot = ((sensor - disp) + 360) % 360
            main.post { texture.setAspect(previewSize.width, previewSize.height); sendView() }
            configureTransform(viewW, viewH)
            cm.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(d: CameraDevice) { device = d; startPreview() }
                override fun onDisconnected(d: CameraDevice) { d.close(); device = null }
                override fun onError(d: CameraDevice, error: Int) { d.close(); device = null; main.post { finish() } }
            }, bg)
        } catch (_: Exception) { finish() }
    }

    @Suppress("DEPRECATION")
    private fun startPreview() {
        val d = device ?: return
        val st = texture.surfaceTexture ?: return
        st.setDefaultBufferSize(previewSize.width, previewSize.height)
        val surface = Surface(st)
        val rd = ImageReader.newInstance(frameSize.width, frameSize.height, ImageFormat.YUV_420_888, 2)
        reader = rd
        try {
            val r = d.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW)
            r.addTarget(surface)
            r.addTarget(rd.surface)
            request = r
            d.createCaptureSession(listOf(surface, rd.surface), object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) { session = s; updateRequest() }
                override fun onConfigureFailed(s: CameraCaptureSession) { main.post { finish() } }
            }, bg)
        } catch (_: Exception) { finish() }
    }

    private fun updateRequest() {
        val r = request ?: return
        val s = session ?: return
        try {
            r.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
            r.set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
            r.set(CaptureRequest.FLASH_MODE, if (torch) CaptureRequest.FLASH_MODE_TORCH else CaptureRequest.FLASH_MODE_OFF)
            sensorRect?.let { a ->
                val z = zoom.coerceIn(1f, maxZoom)
                val w = (a.width() / z).toInt(); val h = (a.height() / z).toInt()
                val l = a.left + (a.width() - w) / 2; val t = a.top + (a.height() - h) / 2
                r.set(CaptureRequest.SCALER_CROP_REGION, Rect(l, t, l + w, t + h))
            }
            s.setRepeatingRequest(r.build(), null, bg)
        } catch (_: Exception) {}
    }

    private fun setZoom(z: Float) { zoom = z.coerceIn(1f, maxZoom); updateRequest() }

    private fun focus() {
        val r = request ?: return
        val s = session ?: return
        try {
            r.set(CaptureRequest.CONTROL_AF_TRIGGER, CaptureRequest.CONTROL_AF_TRIGGER_START)
            s.capture(r.build(), null, bg)
            r.set(CaptureRequest.CONTROL_AF_TRIGGER, CaptureRequest.CONTROL_AF_TRIGGER_IDLE)
        } catch (_: Exception) {}
    }

    private fun closeCamera() {
        try { session?.close() } catch (_: Exception) {}
        try { device?.close() } catch (_: Exception) {}
        try { reader?.close() } catch (_: Exception) {}
        session = null; device = null; request = null; reader = null
    }

    /** The preview keeps the camera's own shape; only upside-down landscape needs turning. */
    @Suppress("DEPRECATION")
    private fun configureTransform(viewW: Int, viewH: Int) {
        if (viewW == 0 || viewH == 0) return
        val m = Matrix()
        if (windowManager.defaultDisplay.rotation == Surface.ROTATION_270)
            m.postRotate(180f, viewW / 2f, viewH / 2f)
        texture.setTransform(m)
    }

    // ── frames → PC (paced by the PC's acknowledgements, like the phone screen) ──
    private val frameTick = object : Runnable {
        override fun run() {
            main.postDelayed(this, 40)
            if (frozen || encoding || session == null) return
            val now = System.currentTimeMillis()
            if (waiting && now - lastSent < 2000) return
            val img = try { reader?.acquireLatestImage() } catch (_: Exception) { null } ?: return
            val w = img.width; val h = img.height
            val nv21 = try { yuvToNv21(img) } catch (_: Exception) { null }
            img.close()
            if (nv21 == null) return
            encoding = true
            encoder.execute {
                try {
                    val out = ByteArrayOutputStream()
                    YuvImage(nv21, ImageFormat.NV21, w, h, null)
                        .compressToJpeg(Rect(0, 0, w, h), if (outWidth >= 1000) 72 else 55, out)
                    if (shotWanted) {
                        shotWanted = false
                        val shot = ByteArrayOutputStream()
                        YuvImage(nv21, ImageFormat.NV21, w, h, null).compressToJpeg(Rect(0, 0, w, h), 92, shot)
                        val b64 = Base64.encodeToString(shot.toByteArray(), Base64.NO_WRAP)
                        main.post { askCaption(b64) }
                    }
                    val send = Bus.send
                    if (send != null) {
                        waiting = true; lastSent = System.currentTimeMillis()
                        send(JSONObject().put("c", "phone_frame")
                            .put("img", Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP))
                            .put("w", w).put("h", h))
                    }
                } catch (_: Exception) {
                } finally { encoding = false }
            }
        }
    }

    private fun yuvToNv21(image: android.media.Image): ByteArray {
        val y = image.planes[0]; val u = image.planes[1]; val v = image.planes[2]
        return Yuv.toNv21(y.buffer, y.rowStride, y.pixelStride, u.buffer, v.buffer, v.rowStride, v.pixelStride, image.width, image.height)
    }

    private fun shoot() {
        shotWanted = true
        status.text = if (L.ku) "📸 وێنە گیرا" else "📸 Photo taken"
        try { (getSystemService(Context.VIBRATOR_SERVICE) as? android.os.Vibrator)?.vibrate(android.os.VibrationEffect.createOneShot(25, android.os.VibrationEffect.DEFAULT_AMPLITUDE)) } catch (_: Exception) {}
    }

    /** Type a few words under the photo (group name, note…), then it appears on the projector. */
    private fun askCaption(b64: String) {
        val et = android.widget.EditText(this).apply {
            hint = if (L.ku) "بۆ نموونە: گرووپی ١" else "e.g. Group 1"
            setText(lastCaption)
            setSingleLine()
        }
        android.app.AlertDialog.Builder(this)
            .setTitle(if (L.ku) "📸 ناونیشانی وێنە" else "📸 Photo caption")
            .setView(et)
            .setPositiveButton(if (L.ku) "پیشاندان" else "Show") { _, _ ->
                lastCaption = et.text.toString().trim()
                send(b64, lastCaption, show = true)
            }
            .setNeutralButton(if (L.ku) "تەنها پاشەکەوت" else "Save only") { _, _ ->
                send(b64, et.text.toString().trim(), show = false)
            }
            .setNegativeButton(if (L.ku) "پاشگەزبوونەوە" else "Cancel", null)
            .show()
    }

    private var lastCaption = ""

    private fun send(b64: String, caption: String, show: Boolean) {
        Bus.send?.invoke(JSONObject().put("c", "shot").put("img", b64).put("label", caption)
            .put("show", show).put("single", true))
    }

    /** 🎤 send the phone's microphone to the PC (off unless you turn it on). */
    private fun toggleMic(b: Button) {
        if (micOn) { micOn = false; b.isSelected = false; Bus.send?.invoke(JSONObject().put("c", "audio_stop")); return }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 2)
            return
        }
        micOn = true; b.isSelected = true
        micThread = Thread({
            var rec: android.media.AudioRecord? = null
            try {
                val rate = 16000
                val min = android.media.AudioRecord.getMinBufferSize(rate,
                    android.media.AudioFormat.CHANNEL_IN_MONO, android.media.AudioFormat.ENCODING_PCM_16BIT)
                rec = android.media.AudioRecord(android.media.MediaRecorder.AudioSource.MIC, rate,
                    android.media.AudioFormat.CHANNEL_IN_MONO, android.media.AudioFormat.ENCODING_PCM_16BIT, maxOf(min, 4096) * 4)
                rec.startRecording()
                val chunk = rate * 2 * 40 / 1000
                val buf = ByteArray(chunk)
                while (micOn) {
                    var got = 0
                    while (got < chunk && micOn) {
                        val n = rec.read(buf, got, chunk - got)
                        if (n <= 0) break
                        got += n
                    }
                    if (got <= 0) continue
                    if ((Bus.backlog?.invoke() ?: 0) > 25) continue
                    Bus.send?.invoke(JSONObject().put("c", "audio")
                        .put("d", Base64.encodeToString(buf, 0, got, Base64.NO_WRAP)).put("r", rate).put("ch", 1))
                }
            } catch (_: Exception) {
            } finally {
                try { rec?.stop(); rec?.release() } catch (_: Exception) {}
            }
        }, "remco-mic").also { it.start() }
    }

    private fun sendView() {
        getSharedPreferences("remco", Context.MODE_PRIVATE).edit().putInt("camRot", rot).apply()
        Bus.send?.invoke(JSONObject().put("c", "phone_view").put("rot", (rot + autoRot) % 360).put("fill", fill))
    }

    override fun onKeyDown(keyCode: Int, event: android.view.KeyEvent): Boolean {
        // volume keys freeze / unfreeze while the camera is open
        if (keyCode == android.view.KeyEvent.KEYCODE_VOLUME_DOWN || keyCode == android.view.KeyEvent.KEYCODE_VOLUME_UP) {
            if (event.repeatCount == 0) frozen = !frozen
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

}

/** A TextureView that keeps the camera's aspect ratio (so the picture is never stretched). */
class AutoFitTextureView(context: Context) : TextureView(context) {
    private var aw = 0
    private var ah = 0

    fun setAspect(w: Int, h: Int) { aw = w; ah = h; requestLayout() }

    override fun onMeasure(widthSpec: Int, heightSpec: Int) {
        super.onMeasure(widthSpec, heightSpec)
        val w = MeasureSpec.getSize(widthSpec)
        val h = MeasureSpec.getSize(heightSpec)
        if (aw == 0 || ah == 0) setMeasuredDimension(w, h)
        else if (w < h * aw / ah) setMeasuredDimension(w, w * ah / aw)
        else setMeasuredDimension(h * aw / ah, h)
    }
}
