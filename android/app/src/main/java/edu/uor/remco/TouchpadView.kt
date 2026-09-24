package edu.uor.remco

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.DashPathEffect
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.os.Handler
import android.os.Looper
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min
import kotlin.math.round

/**
 * The phone's touchpad.
 *
 *  • MOUSE – laptop-style trackpad. With "See the screen" on, the live PC screen
 *    is shown instead and you tap exactly what you want to click (2 fingers =
 *    zoom/move the picture on the phone only).
 *  • ZOOM – works like zooming a photo on the phone: pinch where you want to
 *    zoom, drag to move, double-tap to zoom in / back out. The projector follows.
 *  • Other tools – a frame with the slide's shape mapped 1:1 onto the projected
 *    slide. Two fingers = the same phone-style zoom.
 */
class TouchpadView @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null
) : View(context, attrs) {

    enum class Mode { MOUSE, LASER, SPOTLIGHT, LENS, ZOOM, PEN, HIGHLIGHT, ERASER, NUMBER, TEXT }

    data class PadAnn(
        val kind: String, val x: Float, val y: Float, val color: Int, val text: String, val size: Int,
        val pts: FloatArray? = null, val hl: Boolean = false
    )

    interface Listener {
        fun onRelMove(dx: Float, dy: Float)
        fun onAbs(x: Float, y: Float)
        fun onScreenPoint(x: Float, y: Float)                // mirrored PC screen, 0..1
        fun onButton(button: String, action: String)
        fun onScroll(steps: Int)
        fun onPointer(x: Float, y: Float, phase: String)     // laser / spotlight: start, move, end
        fun onZoom(scale: Float, tx: Float, ty: Float)        // projector zoom changed (tx/ty = fraction of slide)
        fun onPlace(x: Float, y: Float, phase: String)       // number / text: preview, commit, cancel
        fun onInk(x: Float, y: Float, phase: String)         // pen / highlighter: start, move, end
        fun onErase(x: Float, y: Float, phase: String)       // eraser: start, move, end
        fun onHaptic()
    }

    var listener: Listener? = null
    var mode: Mode = Mode.MOUSE
        set(v) { field = v; trail.clear(); touchX = -1f; invalidate() }
    var slideAspect: Float = 16f / 9f
        set(v) { field = if (v > 0.2f && v < 5f) v else 16f / 9f; invalidate() }
    var slideBitmap: Bitmap? = null
        set(v) { field = v; invalidate() }
    /** photo/video on the projector: draw the picture inside the frame like the PC does (fit or fill) */
    var fitBitmap = 0   // 0 = stretch (slides), 1 = fit, 2 = fill
        set(v) { field = v; invalidate() }
    var showSlide = true
        set(v) { field = v; invalidate() }
    var inkColor = Color.RED
        set(v) { field = v; invalidate() }
    var laserColor = Color.parseColor("#ef4444")
    var laserSize = 16
    var spotRadius = 160
    var lensRadius = 140
    var anns: List<PadAnn> = emptyList()
        set(v) { field = v; invalidate() }
    var sensitivity = 1.4f
    /** pen / highlighter thickness in 1080p "css px" (drawn by Remco on the PC) */
    var inkSize = 4

    // projector zoom (web model: scale(s) translate(tx, ty), origin centre)
    var zoomScale = 1f; private set
    var zoomTx = 0f; private set
    var zoomTy = 0f; private set

    // live PC screen (Mouse → "See the screen")
    var mirrorOn = false
        set(v) { field = v; if (!v) { mirror = null; vS = 1f; vTx = 0f; vTy = 0f }; invalidate() }
    var mirror: Bitmap? = null
        set(v) { field = v; invalidate() }
    var mirrorAspect = 16f / 9f
    private var vS = 1f; private var vTx = 0f; private var vTy = 0f   // phone-only view zoom of the mirror

    private val density = resources.displayMetrics.density
    private val slop = 8f * density
    private val placeOffset = 44f * density
    private val main = Handler(Looper.getMainLooper())

    private val bgPaint = Paint().apply { color = Color.parseColor("#151823") }
    private val gridPaint = Paint().apply { color = Color.parseColor("#1F2433"); strokeWidth = 1f }
    private val framePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeWidth = 2f * density; color = Color.parseColor("#4F8CFF")
    }
    private val slidePaint = Paint(Paint.FILTER_BITMAP_FLAG).apply { alpha = 110 }
    private val fullPaint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val text = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE; textAlign = Paint.Align.CENTER; isFakeBoldText = true
    }
    private val trailPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND; strokeJoin = Paint.Join.ROUND
    }
    private val corner = 18f * density
    private val frame = RectF()
    private val tmp = RectF()
    private val holePath = Path()

    // gesture state
    private var downX = 0f; private var downY = 0f
    private var lastX = 0f; private var lastY = 0f
    private var lastT = 0L
    private var downTime = 0L
    private var lastTapTime = 0L; private var lastTapX = 0f; private var lastTapY = 0f
    private var maxPointers = 0
    private var moved = false
    private var dragging = false
    private var pinching = false
    private var ignoreUntilUp = false
    private var scrollAcc = 0f
    private var twoLastY = 0f
    private var touchX = -1f; private var touchY = -1f
    private var placeX = -1f; private var placeY = -1f
    private val trail = ArrayList<Float>()

    // pinch (focal-point zoom, like photos)
    private var pStartDist = 0f
    private var pStartScale = 1f
    private var pAnchorX = 0f; private var pAnchorY = 0f   // content point under the fingers

    private val longPress = Runnable {
        if (moved || maxPointers != 1 || mode != Mode.MOUSE) return@Runnable
        if (mirrorOn) {
            listener?.onHaptic()
            listener?.onScreenPoint(mx(downX), my(downY))
            listener?.onButton("right", "click")
            ignoreUntilUp = true
        } else {
            dragging = true
            listener?.onHaptic()
            listener?.onButton("left", "down")
        }
    }

    // ── zoom maths shared by the projector zoom and the phone-only mirror view ──
    private fun limit(t: Float, s: Float) = t.coerceIn(-(0.5f - 0.5f / s), 0.5f - 0.5f / s)
    private fun contentOf(q: Float, s: Float, t: Float) = 0.5f + (q - 0.5f) / s - t
    private fun screenOf(p: Float, s: Float, t: Float) = 0.5f + s * (p - 0.5f + t)

    fun setZoom(s: Float, tx: Float, ty: Float) {
        zoomScale = s.coerceIn(1f, 4f)
        zoomTx = limit(tx, zoomScale); zoomTy = limit(ty, zoomScale)
        invalidate()
    }

    private fun emitZoom(s: Float, tx: Float, ty: Float) {
        val ns = (round(s * 100f) / 100f).coerceIn(1f, 4f)
        zoomScale = ns
        zoomTx = limit(tx, ns); zoomTy = limit(ty, ns)
        listener?.onZoom(zoomScale, zoomTx, zoomTy)
        invalidate()
    }

    // ────────────────────────────────────────────────────────────────────
    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat(); val h = height.toFloat()
        canvas.drawRoundRect(0f, 0f, w, h, corner, corner, bgPaint)

        if (mode == Mode.MOUSE && mirrorOn) { drawMirror(canvas); return }

        val step = 28f * density
        var x = step
        while (x < w) { canvas.drawLine(x, 0f, x, h, gridPaint); x += step }
        var y = step
        while (y < h) { canvas.drawLine(0f, y, w, y, gridPaint); y += step }

        if (mode == Mode.MOUSE) {
            if (touchX >= 0) {
                fill.color = Color.parseColor("#334F8CFF")
                canvas.drawCircle(touchX, touchY, 28f * density, fill)
            }
            return
        }

        computeFrame(slideAspect)
        val zoomView = mode == Mode.ZOOM
        val s = if (zoomView) zoomScale else 1f
        val tx = if (zoomView) zoomTx else 0f
        val ty = if (zoomView) zoomTy else 0f
        fun px(p: Float) = frame.left + screenOf(p, s, tx) * frame.width()
        fun py(p: Float) = frame.top + screenOf(p, s, ty) * frame.height()

        canvas.save()
        canvas.clipRect(frame)
        val bmp = slideBitmap
        if (showSlide && bmp != null) {
            tmp.set(px(0f), py(0f), px(1f), py(1f))
            if (fitBitmap != 0 && bmp.width > 0 && bmp.height > 0) {
                val sc = if (fitBitmap == 2) max(tmp.width() / bmp.width, tmp.height() / bmp.height)
                         else min(tmp.width() / bmp.width, tmp.height() / bmp.height)
                val w = bmp.width * sc; val h = bmp.height * sc
                val cx = tmp.centerX(); val cy = tmp.centerY()
                tmp.set(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2)
            }
            canvas.drawBitmap(bmp, null, tmp, if (zoomView || fitBitmap != 0) fullPaint else slidePaint)
        }
        // annotations already on the slide
        val k = frame.height() / 1080f * s
        for (pass in 0..1) for (a in anns) {
            if (a.kind != "ink" || a.hl != (pass == 0)) continue
            val pts = a.pts ?: continue
            trailPaint.color = a.color
            trailPaint.alpha = if (a.hl) 110 else 255
            trailPaint.strokeWidth = max(1.5f, a.size * k * 1.6f)
            if (pts.size < 4) { canvas.drawPoint(px(pts[0]), py(pts[1]), trailPaint); continue }
            holePath.reset()
            holePath.moveTo(px(pts[0]), py(pts[1]))
            var j = 2
            while (j + 1 < pts.size) { holePath.lineTo(px(pts[j]), py(pts[j + 1])); j += 2 }
            canvas.drawPath(holePath, trailPaint)
        }
        for (a in anns) {
            if (a.kind == "ink") continue
            val ax = px(a.x); val ay = py(a.y)
            if (a.kind == "number") {
                val r = max(8f * density, a.size * (36f / 28f) * k * 1.6f) / 2f
                fill.color = a.color
                canvas.drawCircle(ax, ay, r, fill)
                stroke.color = Color.WHITE; stroke.strokeWidth = 1.5f * density
                canvas.drawCircle(ax, ay, r, stroke)
                text.textSize = r * 1.1f
                canvas.drawText(a.text, ax, ay + text.textSize * 0.36f, text)
            } else {
                val label = if (a.text.length > 14) a.text.take(13) + "…" else a.text
                text.textSize = 10f * density
                val tw = text.measureText(label) + 10f * density
                val th = 16f * density
                tmp.set(ax - 0.08f * tw, ay - 1.1f * th, ax - 0.08f * tw + tw, ay - 1.1f * th + th)
                fill.color = if (a.color == Color.TRANSPARENT) Color.parseColor("#66000000") else a.color
                canvas.drawRoundRect(tmp, 5f * density, 5f * density, fill)
                text.color = if (a.color == Color.BLACK || a.color == Color.TRANSPARENT) Color.WHITE else Color.parseColor("#111827")
                canvas.drawText(label, tmp.centerX(), tmp.centerY() + text.textSize * 0.35f, text)
                text.color = Color.WHITE
            }
        }
        canvas.restore()

        // other tools: show which part of the slide the projector is zoomed into
        if (!zoomView && (zoomScale > 1.001f || abs(zoomTx) > 0.001f || abs(zoomTy) > 0.001f)) {
            val l = contentOf(0f, zoomScale, zoomTx); val t = contentOf(0f, zoomScale, zoomTy)
            tmp.set(frame.left + l * frame.width(), frame.top + t * frame.height(),
                frame.left + (l + 1f / zoomScale) * frame.width(), frame.top + (t + 1f / zoomScale) * frame.height())
            stroke.color = Color.parseColor("#FFD60A"); stroke.strokeWidth = 2f * density
            stroke.pathEffect = DashPathEffect(floatArrayOf(8f * density, 5f * density), 0f)
            canvas.drawRect(tmp, stroke)
            stroke.pathEffect = null
        }
        if (zoomScale > 1.001f) {
            text.textSize = 12f * density; text.color = Color.parseColor("#FFD60A")
            canvas.drawText(String.format("%.1f×", zoomScale), frame.right - 22f * density, frame.top + 16f * density, text)
            text.color = Color.WHITE
        }

        canvas.drawRect(frame, framePaint)

        // spotlight preview
        if (mode == Mode.LENS && touchX >= 0) {
            val r = max(14f * density, lensRadius * (frame.height() / 1080f) * 1.6f)
            stroke.color = Color.WHITE; stroke.strokeWidth = 3f * density
            canvas.drawCircle(touchX, touchY, r, stroke)
            stroke.color = Color.parseColor("#66000000"); stroke.strokeWidth = 1f * density
            canvas.drawCircle(touchX, touchY, r + 2.5f * density, stroke)
        }
        if (mode == Mode.SPOTLIGHT && touchX >= 0) {
            val r = max(14f * density, spotRadius * (frame.height() / 1080f) * 1.6f)
            holePath.reset()
            holePath.fillType = Path.FillType.EVEN_ODD
            holePath.addRect(frame, Path.Direction.CW)
            holePath.addCircle(touchX, touchY, r, Path.Direction.CW)
            fill.color = Color.parseColor("#B3000000")
            canvas.save(); canvas.clipRect(frame); canvas.drawPath(holePath, fill); canvas.restore()
        }

        // live ink trail
        if (trail.size >= 4 && (mode == Mode.PEN || mode == Mode.HIGHLIGHT)) {
            trailPaint.color = inkColor
            trailPaint.alpha = if (mode == Mode.HIGHLIGHT) 110 else 230
            trailPaint.strokeWidth = max(2f * density, inkSize * (frame.height() / 1080f) * 1.6f)
            var i = 2
            while (i + 1 < trail.size) {
                canvas.drawLine(trail[i - 2], trail[i - 1], trail[i], trail[i + 1], trailPaint)
                i += 2
            }
        }

        // placement preview for number / text
        if ((mode == Mode.NUMBER || mode == Mode.TEXT) && placeX >= 0) {
            stroke.color = Color.WHITE; stroke.strokeWidth = 2f * density
            stroke.pathEffect = DashPathEffect(floatArrayOf(5f * density, 4f * density), 0f)
            canvas.drawCircle(placeX, placeY, 11f * density, stroke)
            stroke.pathEffect = null
            fill.color = Color.parseColor("#66FFFFFF"); fill.strokeWidth = 1.5f * density
            canvas.drawLine(placeX, placeY + 11f * density, touchX, touchY, fill)
        }

        if (touchX >= 0 && (mode == Mode.LASER || mode == Mode.PEN || mode == Mode.HIGHLIGHT || mode == Mode.ERASER)) {
            fill.color = when (mode) {
                Mode.LASER -> laserColor
                Mode.ERASER -> Color.WHITE
                else -> inkColor
            }
            val r = if (mode == Mode.LASER) max(4f * density, laserSize * (frame.height() / 1080f) * 1.6f / 2f) else 7f * density
            canvas.drawCircle(touchX, touchY, r, fill)
        }
    }

    private fun drawMirror(canvas: Canvas) {
        computeFrame(mirrorAspect)
        val bmp = mirror
        canvas.save()
        canvas.clipRect(frame)
        if (bmp != null) {
            tmp.set(frame.left + screenOf(0f, vS, vTx) * frame.width(), frame.top + screenOf(0f, vS, vTy) * frame.height(),
                frame.left + screenOf(1f, vS, vTx) * frame.width(), frame.top + screenOf(1f, vS, vTy) * frame.height())
            canvas.drawBitmap(bmp, null, tmp, fullPaint)
        } else {
            text.textSize = 13f * density; text.color = Color.parseColor("#8A90A2")
            canvas.drawText("…", frame.centerX(), frame.centerY(), text)
            text.color = Color.WHITE
        }
        canvas.restore()
        canvas.drawRect(frame, framePaint)
        if (touchX >= 0) {
            stroke.color = Color.parseColor("#FFD60A"); stroke.strokeWidth = 2f * density
            canvas.drawCircle(touchX, touchY, 14f * density, stroke)
        }
    }

    private fun computeFrame(aspect: Float) {
        val pad = 6f * density
        val w = width - 2 * pad; val h = height - 2 * pad
        var fw = w; var fh = w / aspect
        if (fh > h) { fh = h; fw = h * aspect }
        val l = (width - fw) / 2f; val t = (height - fh) / 2f
        frame.set(l, t, l + fw, t + fh)
    }

    private fun qx(x: Float) = ((x - frame.left) / frame.width()).coerceIn(0f, 1f)
    private fun qy(y: Float) = ((y - frame.top) / frame.height()).coerceIn(0f, 1f)
    /** pad point → PC-screen point (0..1) in mirror mode, through the phone-only view zoom */
    private fun mx(x: Float) = contentOf(qx(x), vS, vTx).coerceIn(0f, 1f)
    private fun my(y: Float) = contentOf(qy(y), vS, vTy).coerceIn(0f, 1f)

    override fun onTouchEvent(e: MotionEvent): Boolean {
        parent?.requestDisallowInterceptTouchEvent(true)
        when {
            mode == Mode.MOUSE && mirrorOn -> { computeFrame(mirrorAspect); handleMirror(e) }
            mode == Mode.MOUSE -> handleMouse(e)
            else -> { computeFrame(slideAspect); handleFramed(e) }
        }
        return true
    }

    // ── two-finger focal zoom (projector zoom, or the phone-only mirror view) ──
    private fun focalX(e: MotionEvent) = (e.getX(0) + e.getX(1)) / 2f
    private fun focalY(e: MotionEvent) = (e.getY(0) + e.getY(1)) / 2f
    private fun dist(e: MotionEvent) = if (e.pointerCount < 2) 0f else hypot(e.getX(0) - e.getX(1), e.getY(0) - e.getY(1))

    private fun pinchStart(e: MotionEvent, mirrorView: Boolean) {
        pinching = true
        pStartDist = dist(e)
        val s = if (mirrorView) vS else zoomScale
        val tx = if (mirrorView) vTx else zoomTx
        val ty = if (mirrorView) vTy else zoomTy
        pStartScale = s
        pAnchorX = contentOf(qx(focalX(e)), s, tx)
        pAnchorY = contentOf(qy(focalY(e)), s, ty)
    }

    private fun pinchMove(e: MotionEvent, mirrorView: Boolean) {
        if (e.pointerCount < 2 || pStartDist < 1f) return
        val s = (pStartScale * dist(e) / pStartDist).coerceIn(1f, 4f)
        // keep the content point that was under the fingers under the fingers (pans too)
        val tx = (qx(focalX(e)) - 0.5f) / s - (pAnchorX - 0.5f)
        val ty = (qy(focalY(e)) - 0.5f) / s - (pAnchorY - 0.5f)
        if (mirrorView) { vS = s; vTx = limit(tx, s); vTy = limit(ty, s); invalidate() }
        else emitZoom(s, tx, ty)
    }

    // ── framed tools ────────────────────────────────────────────────────
    private fun handleFramed(e: MotionEvent) {
        val l = listener ?: return
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                ignoreUntilUp = false; pinching = false; moved = false
                trail.clear()
                touchX = e.x; touchY = e.y; lastX = e.x; lastY = e.y; downX = e.x; downY = e.y
                downTime = e.eventTime
                when (mode) {
                    Mode.LASER, Mode.SPOTLIGHT, Mode.LENS -> l.onPointer(qx(e.x), qy(e.y), "start")
                    Mode.PEN, Mode.HIGHLIGHT -> { trail.add(e.x); trail.add(e.y); l.onInk(qx(e.x), qy(e.y), "start") }
                    Mode.ERASER -> l.onErase(qx(e.x), qy(e.y), "start")
                    Mode.NUMBER, Mode.TEXT -> { place(e.x, e.y); l.onPlace(qx(placeX), qy(placeY), "preview") }
                    else -> {}
                }
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                if (e.pointerCount == 2 && !pinching) {
                    endSingle(cancel = true)
                    pinchStart(e, false)
                }
            }
            MotionEvent.ACTION_MOVE -> {
                if (pinching) { pinchMove(e, false); return }
                if (ignoreUntilUp) return
                if (!moved && hypot(e.x - downX, e.y - downY) > slop) moved = true
                touchX = e.x; touchY = e.y
                when (mode) {
                    Mode.LASER, Mode.SPOTLIGHT, Mode.LENS -> l.onPointer(qx(e.x), qy(e.y), "move")
                    Mode.PEN, Mode.HIGHLIGHT -> { trail.add(e.x); trail.add(e.y); l.onInk(qx(e.x), qy(e.y), "move") }
                    Mode.ERASER -> l.onErase(qx(e.x), qy(e.y), "move")
                    Mode.ZOOM -> if (zoomScale > 1.001f) {
                        // drag = move around, the picture follows the finger
                        emitZoom(zoomScale, zoomTx + (e.x - lastX) / frame.width() / zoomScale,
                            zoomTy + (e.y - lastY) / frame.height() / zoomScale)
                    }
                    Mode.NUMBER, Mode.TEXT -> { place(e.x, e.y); l.onPlace(qx(placeX), qy(placeY), "preview") }
                    else -> {}
                }
                lastX = e.x; lastY = e.y
            }
            MotionEvent.ACTION_POINTER_UP -> {
                if (pinching && e.pointerCount <= 2) { pinching = false; ignoreUntilUp = true }
            }
            MotionEvent.ACTION_UP -> {
                if (pinching) pinching = false
                else if (!ignoreUntilUp) {
                    if (mode == Mode.ZOOM && !moved && e.eventTime - downTime < 250) zoomTap(e.x, e.y, e.eventTime)
                    endSingle(cancel = false)
                }
                ignoreUntilUp = false
                touchX = -1f; touchY = -1f; placeX = -1f; placeY = -1f
                main.postDelayed({ if (touchX < 0) { trail.clear(); invalidate() } }, 900)
            }
            MotionEvent.ACTION_CANCEL -> {
                if (pinching) pinching = false else endSingle(cancel = true)
                touchX = -1f; touchY = -1f; placeX = -1f; placeY = -1f
            }
        }
        invalidate()
    }

    /** Double-tap in Zoom: zoom in 2.5× on that spot, or back out — like photos. */
    private fun zoomTap(x: Float, y: Float, t: Long) {
        val isDouble = t - lastTapTime < 320 && hypot(x - lastTapX, y - lastTapY) < 40f * density
        lastTapTime = if (isDouble) 0L else t
        lastTapX = x; lastTapY = y
        if (!isDouble) return
        listener?.onHaptic()
        if (zoomScale > 1.05f) emitZoom(1f, 0f, 0f)
        else {
            val s = 2.5f
            val px = contentOf(qx(x), zoomScale, zoomTx); val py = contentOf(qy(y), zoomScale, zoomTy)
            emitZoom(s, (qx(x) - 0.5f) / s - (px - 0.5f), (qy(y) - 0.5f) / s - (py - 0.5f))
        }
    }

    private fun place(x: Float, y: Float) {
        placeX = x.coerceIn(frame.left, frame.right)
        placeY = (y - placeOffset).coerceIn(frame.top, frame.bottom)
    }

    private fun endSingle(cancel: Boolean) {
        val l = listener ?: return
        when (mode) {
            Mode.LASER, Mode.SPOTLIGHT, Mode.LENS -> l.onPointer(qx(max(0f, touchX)), qy(max(0f, touchY)), "end")
            Mode.PEN, Mode.HIGHLIGHT -> l.onInk(0f, 0f, "end")
            Mode.ERASER -> l.onErase(0f, 0f, "end")
            Mode.NUMBER, Mode.TEXT -> if (placeX >= 0) {
                if (cancel) l.onPlace(0f, 0f, "cancel")
                else { l.onHaptic(); l.onPlace(qx(placeX), qy(placeY), "commit") }
            }
            else -> {}
        }
        placeX = -1f; placeY = -1f
    }

    // ── Mouse + "See the screen": tap exactly what you want ─────────────
    private fun handleMirror(e: MotionEvent) {
        val l = listener ?: return
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = e.x; downY = e.y; downTime = e.eventTime
                maxPointers = 1; moved = false; dragging = false; pinching = false; ignoreUntilUp = false
                touchX = e.x; touchY = e.y
                main.postDelayed(longPress, 500)
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                maxPointers = max(maxPointers, e.pointerCount)
                main.removeCallbacks(longPress)
                if (dragging) { l.onButton("left", "up"); dragging = false }
                if (e.pointerCount == 2) pinchStart(e, true)
            }
            MotionEvent.ACTION_MOVE -> {
                if (pinching) { pinchMove(e, true); return }
                if (ignoreUntilUp || maxPointers > 1) return
                touchX = e.x; touchY = e.y
                if (!moved && hypot(e.x - downX, e.y - downY) > slop) {
                    moved = true
                    main.removeCallbacks(longPress)
                    // start a drag from where the finger went down
                    l.onScreenPoint(mx(downX), my(downY))
                    l.onButton("left", "down")
                    dragging = true
                }
                if (dragging) l.onScreenPoint(mx(e.x), my(e.y))
            }
            MotionEvent.ACTION_POINTER_UP -> {
                if (pinching && e.pointerCount <= 2) { pinching = false; ignoreUntilUp = true }
            }
            MotionEvent.ACTION_UP -> {
                main.removeCallbacks(longPress)
                if (dragging) l.onButton("left", "up")
                else if (!ignoreUntilUp && !moved && maxPointers == 1 && e.eventTime - downTime < 400) {
                    l.onHaptic()
                    val second = e.eventTime - lastTapTime < 450 && hypot(e.x - lastTapX, e.y - lastTapY) < 36f * density
                    if (second) {
                        // don't move: Windows needs both clicks on the same pixel to see a double-click
                        l.onButton("left", "click")
                        lastTapTime = 0L
                    } else {
                        l.onScreenPoint(mx(e.x), my(e.y))
                        l.onButton("left", "click")
                        lastTapTime = e.eventTime; lastTapX = e.x; lastTapY = e.y
                    }
                }
                dragging = false; pinching = false; ignoreUntilUp = false
                touchX = -1f; touchY = -1f
            }
            MotionEvent.ACTION_CANCEL -> {
                main.removeCallbacks(longPress)
                if (dragging) l.onButton("left", "up")
                dragging = false; pinching = false
                touchX = -1f; touchY = -1f
            }
        }
        invalidate()
    }

    // ── Mouse (trackpad) ────────────────────────────────────────────────
    private fun handleMouse(e: MotionEvent) {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = e.x; downY = e.y; lastX = e.x; lastY = e.y
                downTime = e.eventTime; lastT = e.eventTime
                maxPointers = 1; moved = false; dragging = false; scrollAcc = 0f
                touchX = e.x; touchY = e.y
                main.postDelayed(longPress, 450)
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                maxPointers = max(maxPointers, e.pointerCount)
                twoLastY = avgY(e)
                main.removeCallbacks(longPress)
            }
            MotionEvent.ACTION_MOVE -> {
                if (e.pointerCount >= 2) {
                    val ay = avgY(e)
                    val dy = ay - twoLastY
                    twoLastY = ay
                    if (abs(ay - downY) > slop) moved = true
                    scrollAcc += dy
                    val stepPx = 36f * density
                    val steps = (scrollAcc / stepPx).toInt()
                    if (steps != 0) {
                        scrollAcc -= steps * stepPx
                        listener?.onScroll(steps)
                    }
                } else if (maxPointers == 1) {
                    val dx = e.x - lastX; val dy = e.y - lastY
                    if (!moved && hypot(e.x - downX, e.y - downY) > slop) {
                        moved = true
                        if (!dragging) main.removeCallbacks(longPress)
                    }
                    if (moved || dragging) {
                        val dt = max(1L, e.eventTime - lastT).toFloat()
                        val speed = hypot(dx, dy) / density / dt
                        val accel = 1f + min(speed * 1.2f, 2.5f)
                        val k = sensitivity * accel / density * 1.6f
                        listener?.onRelMove(dx * k, dy * k)
                    }
                    lastX = e.x; lastY = e.y; lastT = e.eventTime
                    touchX = e.x; touchY = e.y
                }
            }
            MotionEvent.ACTION_UP -> {
                main.removeCallbacks(longPress)
                val quick = e.eventTime - downTime < 280
                if (dragging) listener?.onButton("left", "up")
                else if (!moved && quick) {
                    listener?.onHaptic()
                    listener?.onButton(if (maxPointers >= 2) "right" else "left", "click")
                }
                dragging = false
                touchX = -1f; touchY = -1f
            }
            MotionEvent.ACTION_CANCEL -> {
                main.removeCallbacks(longPress)
                if (dragging) listener?.onButton("left", "up")
                dragging = false
                touchX = -1f; touchY = -1f
            }
        }
        invalidate()
    }

    private fun avgY(e: MotionEvent): Float {
        var s = 0f
        for (i in 0 until e.pointerCount) s += e.getY(i)
        return s / e.pointerCount
    }
}
