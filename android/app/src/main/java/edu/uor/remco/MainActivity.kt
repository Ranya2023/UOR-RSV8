package edu.uor.remco

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import android.provider.Settings
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.util.Base64
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.media.projection.MediaProjectionManager
import android.view.MotionEvent
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.InputMethodManager
import android.net.wifi.WifiManager
import org.json.JSONArray
import android.widget.CheckBox
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import org.json.JSONObject
import java.util.Locale
import kotlin.math.abs

private typealias Mode = TouchpadView.Mode

private const val KB_SENTINEL = " "

class MainActivity : Activity(), Link.Listener, WifiSide.Callback, TouchpadView.Listener, Files.Ui {

    private lateinit var prefs: SharedPreferences
    private lateinit var link: Link
    private lateinit var wifiSide: WifiSide
    private val beacons = LinkedHashMap<String, WifiSide.Beacon>()
    private val pinAsked = HashSet<String>()
    private val firstReply = HashMap<String, Long>()
    private var connKind: Link.Kind? = null
    private val main = Handler(Looper.getMainLooper())

    // views
    private lateinit var root: LinearLayout
    private lateinit var statusDot: TextView
    private lateinit var statusText: TextView
    private lateinit var btnLang: Button
    private lateinit var btnConnect: Button
    private lateinit var slideInfo: TextView
    private lateinit var animInfo: TextView
    private lateinit var timerText: TextView
    private lateinit var previewRow: View
    private lateinit var imgCur: ImageView
    private lateinit var imgNext: ImageView
    private lateinit var lblCur: TextView
    private lateinit var lblNext: TextView
    private lateinit var notesPanel: ScrollView
    private lateinit var notesText: TextView
    private lateinit var optionsPanel: LinearLayout
    private lateinit var pad: TouchpadView
    private lateinit var padHint: TextView
    private lateinit var toolButtons: Map<Mode, Button>
    private lateinit var flash: TextView

    // state from the PC
    private var everConnected = false
    private var connectedName = ""
    private var pptOpen = false
    private var showing = false
    private var slide = 0
    private var total = 0
    private var screenMode = "normal"
    private var notes = ""
    private var titles: List<String> = emptyList()

    // ── tool settings (same ranges & colours as the web app) ────────────
    private var mode = Mode.MOUSE
    private val inkColors = intArrayOf(
        Color.parseColor("#FF3B30"), Color.parseColor("#FFD60A"), Color.parseColor("#34C759"),
        Color.parseColor("#0A84FF"), Color.WHITE, Color.BLACK
    )
    private var inkColor = inkColors[0]

    private val laserColors = listOf("#ef4444", "#22c55e", "#3b82f6", "#eab308", "#a855f7", "#ffffff")
    private var laserSize = 16          // 8 … 48
    private var laserColor = "#ef4444"
    private var laserLabel = ""

    private val spotStyles = listOf(
        "classic" to "🎴 Classic", "theater" to "🎭 Theater", "minimal" to "⚪ Minimal", "glass" to "🧊 Glass",
        "stage" to "🔦 Stage", "neon" to "💫 Neon", "celebration" to "🎉 Celebrate", "colorful" to "🫟 Colorful"
    )
    private val spotColors = listOf("#ef4444", "#3b82f6", "#22c55e", "#eab308", "#a855f7", "#ec4899")
    private var spotRadius = 160        // 60 … 400
    private var lensRadius = 140        // 60 … 400
    private var lensZoom = 2.0          // 1.5 … 4
    private var spotStyle = "stage"
    private var spotConfetti = true
    private var spotColor: String? = null

    private var mirrorOn = false
    private var penSize = 4             // 1 … 30
    private var hlSize = 18             // 6 … 48
    private val inkBuf = ArrayList<Double>()
    private lateinit var kbInput: EditText
    private var kbOpen = false
    private var kbLast = KB_SENTINEL
    private var kbResetting = false
    private var hotspot: WifiManager.LocalOnlyHotspotReservation? = null
    private lateinit var files: Files
    private lateinit var joiner: WifiJoiner
    private var slideThumb: Bitmap? = null
    private var mediaUris: List<android.net.Uri> = emptyList()
    private var lastMediaIndex = -2
    private val pendingShare = ArrayList<android.net.Uri>()
    private val hideTransfer = Runnable { findViewById<View>(R.id.transferBar).visibility = View.GONE }
    private var lastVolStep = 0L

    private var numberSize = 28         // 16 … 64

    private val textColors = listOf("#111827", "#ffffff", "#ef4444", "#3b82f6", "#22c55e", "#eab308")
    private val bgColors = listOf("#eab308", "#ef4444", "#3b82f6", "#22c55e", "#f97316", "#ffffff", "#000000")
    private var textStyle = "pin"
    private var textFont = 14           // 10 … 32
    private var textColor = "#111827"
    private var textBg = "#eab308"      // or "transparent" (plain only)

    // pointer coalescing – at most one message of each kind every 16 ms
    private var pendDx = 0f
    private var pendDy = 0f
    private val pending = LinkedHashMap<String, JSONObject>()
    private var flushScheduled = false
    private val flushRunnable = Runnable { flushScheduled = false; flushMoves() }

    // timer
    private var timerMode = "down"
    private var timerTotal = 0
    private var timerSec = -1
    private var timerRunning = false
    private var timerOnProjector = false
    private var timerSilent = false
    private val timerTick = object : Runnable {
        override fun run() {
            if (!timerRunning) return
            if (timerMode == "down") {
                timerSec = maxOf(0, timerSec - 1)
                checkTimerAlerts()
                if (timerSec == 0) timerRunning = false
            } else timerSec++
            renderTimer()
            sendTimer()
            if (timerRunning) main.postDelayed(this, 1000)
        }
    }

    // ────────────────────────────────────────────────────────────────────
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_main)
        prefs = getSharedPreferences("remco", Context.MODE_PRIVATE)
        L.ku = prefs.getBoolean("ku", true)
        loadToolPrefs()
        link = Link(this)
        wifiSide = WifiSide(this, this)

        root = findViewById(R.id.root)
        statusDot = findViewById(R.id.statusDot)
        statusText = findViewById(R.id.statusText)
        btnLang = findViewById(R.id.btnLang)
        btnConnect = findViewById(R.id.btnConnect)
        slideInfo = findViewById(R.id.slideInfo)
        animInfo = findViewById(R.id.animInfo)
        timerText = findViewById(R.id.timerText)
        previewRow = findViewById(R.id.previewRow)
        imgCur = findViewById(R.id.imgCur)
        imgNext = findViewById(R.id.imgNext)
        lblCur = findViewById(R.id.lblCur)
        lblNext = findViewById(R.id.lblNext)
        notesPanel = findViewById(R.id.notesPanel)
        notesText = findViewById(R.id.notesText)
        optionsPanel = findViewById(R.id.optionsPanel)
        pad = findViewById(R.id.pad)
        padHint = findViewById(R.id.padHint)
        kbInput = findViewById(R.id.kbInput)
        setupMouseBar()
        setupKeyboard()

        // big threshold flash (59, 3, 2, 1, ⏰) over everything
        flash = TextView(this).apply {
            gravity = Gravity.CENTER
            setBackgroundColor(Color.parseColor("#80000000"))
            typeface = Typeface.MONOSPACE
            setTypeface(typeface, Typeface.BOLD)
            textSize = 150f
            visibility = View.GONE
        }
        addContentView(flash, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        pad.listener = this
        pad.sensitivity = prefs.getFloat("sens", 1.4f)
        pad.showSlide = prefs.getBoolean("showSlide", true)
        previewRow.visibility = if (prefs.getBoolean("previews", true)) View.VISIBLE else View.GONE
        syncPad()

        toolButtons = mapOf(
            Mode.MOUSE to findViewById(R.id.toolMouse),
            Mode.LASER to findViewById(R.id.toolLaser),
            Mode.SPOTLIGHT to findViewById(R.id.toolSpot),
            Mode.LENS to findViewById(R.id.toolLens),
            Mode.ZOOM to findViewById(R.id.toolZoom),
            Mode.PEN to findViewById(R.id.toolPen),
            Mode.HIGHLIGHT to findViewById(R.id.toolHigh),
            Mode.ERASER to findViewById(R.id.toolEraser),
            Mode.NUMBER to findViewById(R.id.toolNumber),
            Mode.TEXT to findViewById(R.id.toolText)
        )
        toolButtons.forEach { (m, b) -> b.setOnClickListener { selectTool(m) } }

        btnLang.setOnClickListener {
            L.ku = !L.ku
            prefs.edit().putBoolean("ku", L.ku).apply()
            applyTexts()
        }
        btnConnect.setOnClickListener {
            if (link.isConnected || connectedName.isNotEmpty()) {
                link.disconnect()
                prefs.edit().remove("lastAddr").remove("pcId").remove("pcBtAuto").apply()
                connKind = null
                connectedName = ""
                setStatus(false, L.t("notConnected"))
            } else chooseDevice()
        }

        findViewById<Button>(R.id.btnPrev).setOnClickListener { haptic(); cmd("prev") }
        findViewById<Button>(R.id.btnNext).setOnClickListener { haptic(); cmd("next") }
        findViewById<Button>(R.id.btnStart).setOnClickListener { cmd("start", "from", "begin") }
        findViewById<Button>(R.id.btnStartCur).setOnClickListener { cmd("start", "from", "current") }
        findViewById<Button>(R.id.btnEnd).setOnClickListener { cmd("end") }
        findViewById<Button>(R.id.btnBlack).setOnClickListener { cmd("screen", "m", "black") }
        findViewById<Button>(R.id.btnWhite).setOnClickListener { cmd("screen", "m", "white") }
        findViewById<Button>(R.id.btnClear).setOnClickListener { haptic(); cmd("clear") }
        findViewById<Button>(R.id.btnSlides).setOnClickListener { showSlidesDialog() }
        findViewById<Button>(R.id.btnSettings).setOnClickListener { showSettings() }
        findViewById<Button>(R.id.btnNotes).setOnClickListener {
            notesPanel.visibility = if (notesPanel.visibility == View.VISIBLE) View.GONE else View.VISIBLE
            it.isSelected = notesPanel.visibility == View.VISIBLE
        }
        imgCur.setOnClickListener { findViewById<Button>(R.id.btnNotes).performClick() }
        timerText.setOnClickListener { showTimerDialog() }
        timerText.setOnLongClickListener { resetTimer(); true }

        applyTexts()
        selectTool(Mode.MOUSE)
        setStatus(false, L.t("notConnected"))
        renderState()
        renderTimer()

        Bus.send = { o -> link.send(o) }
        Bus.backlog = { link.backlog() }
        Bus.volume = { up -> volumeStep(up) }
        Bus.isConnected = { link.isConnected }
        files = Files(this, { o -> link.send(o) }, this)
        findViewById<Button>(R.id.btnFiles).setOnClickListener { showFilesDialog() }
        findViewById<Button>(R.id.btnQuiz).setOnClickListener { quizDialog() }
        findViewById<Button>(R.id.btnPicker).setOnClickListener { pickerDialog() }
        joiner = WifiJoiner(this)
        setupMediaBar()
        handleShareIntent(intent)
        Bus.onPhoneScreenChanged = { on -> main.post {
            if (on) { sendPhoneView(); showing2 = "phone"; if (Bus.audioOn) quietPhone(true) }
            else { unmutePhone(); quietPhone(false); if (showing2 == "phone") showing2 = "ppt" }
            if (mode == Mode.MOUSE) buildOptions()
        } }

        wifiSide.start()
        setStatus(null, L.t("searching"))
        // Give Wi-Fi ~2.5 s to find the PC; otherwise start on Bluetooth (Wi-Fi still takes over later).
        main.postDelayed({ if (!link.isConnected && link.bluetoothTarget == null) autoConnectBt() }, 2500)
    }

    override fun onDestroy() {
        wifiSide.stop()
        joiner.release()
        stopPhoneHotspot()
        if (Bus.phoneScreenOn) stopService(Intent(this, ProjectionService::class.java))
        Bus.send = null
        Bus.backlog = null
        Bus.onPhoneScreenChanged = null
        files.cancelAll()
        unmutePhone()
        RemoteService.stop(this)
        Bus.volume = null
        Bus.isConnected = null
        link.disconnect()
        main.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    // ── prefs ───────────────────────────────────────────────────────────
    private fun loadToolPrefs() {
        laserSize = prefs.getInt("laserSize", 16)
        laserColor = prefs.getString("laserColor", "#ef4444") ?: "#ef4444"
        spotRadius = prefs.getInt("spotRadius", 160)
        lensRadius = prefs.getInt("lensRadius", 140)
        lensZoom = prefs.getFloat("lensZoom", 2f).toDouble()
        spotStyle = prefs.getString("spotStyle", "stage") ?: "stage"
        spotConfetti = prefs.getBoolean("spotConfetti", true)
        spotColor = prefs.getString("spotColor", null)
        numberSize = prefs.getInt("numberSize", 28)
        textStyle = prefs.getString("textStyle", "pin") ?: "pin"
        textFont = prefs.getInt("textFont", 14)
        textColor = prefs.getString("textColor", "#111827") ?: "#111827"
        textBg = prefs.getString("textBg", "#eab308") ?: "#eab308"
        inkColor = prefs.getInt("inkColor", inkColors[0])
        penSize = prefs.getInt("penSize", 4)
        hlSize = prefs.getInt("hlSize", 18)
        timerMode = prefs.getString("timerMode", "down") ?: "down"
        timerOnProjector = prefs.getBoolean("timerProj", false)
        timerSilent = prefs.getBoolean("timerSilent", false)
    }

    private fun saveToolPrefs() {
        prefs.edit()
            .putInt("laserSize", laserSize).putString("laserColor", laserColor)
            .putInt("spotRadius", spotRadius).putString("spotStyle", spotStyle)
            .putInt("lensRadius", lensRadius).putFloat("lensZoom", lensZoom.toFloat())
            .putBoolean("spotConfetti", spotConfetti).putString("spotColor", spotColor)
            .putInt("numberSize", numberSize).putString("textStyle", textStyle)
            .putInt("textFont", textFont).putString("textColor", textColor).putString("textBg", textBg)
            .putInt("inkColor", inkColor)
            .putInt("penSize", penSize).putInt("hlSize", hlSize)
            .apply()
    }

    private fun syncPad() {
        pad.laserColor = Color.parseColor(laserColor)
        pad.laserSize = laserSize
        pad.spotRadius = spotRadius
        pad.lensRadius = lensRadius
        pad.inkColor = inkColor
        pad.invalidate()
    }

    // ── Permissions & device choice ─────────────────────────────────────
    private fun ensurePermission(): Boolean {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), 42)
            return false
        }
        return true
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 45) {
            if (grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED) chooseDevice()
            return
        }
        if (requestCode == 44) {
            val ok = grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED
            if (ok && Bus.phoneScreenOn) Bus.setAudio?.invoke(true)
            if (pendingScreenAfterPerm) { pendingScreenAfterPerm = false; togglePhoneScreen() }
            buildOptions()
            return
        }
        if (requestCode == 43) {
            if (grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED) startPhoneHotspot()
            else toast(L.t("hsNeedPerm"))
            return
        }
        if (requestCode != 42) return
        if (grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
            if (!autoConnectBt()) chooseDevice()
        } else toast(L.t("needPerm"))
    }

    private fun adapter(): BluetoothAdapter? =
        (getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    private fun connMode(): String = prefs.getString("connMode", "auto") ?: "auto"

    private fun hasBtPermission(): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

    /** The saved PC's Bluetooth device, if it is paired with this phone. */
    @Suppress("MissingPermission")
    private fun savedBtDevice(): BluetoothDevice? {
        if (!hasBtPermission()) return null
        val ad = adapter() ?: return null
        if (!ad.isEnabled) return null
        val addrs = listOfNotNull(prefs.getString("lastAddr", null), prefs.getString("pcBtAuto", null))
        if (addrs.isEmpty()) return null
        return try { ad.bondedDevices.firstOrNull { d -> addrs.any { it.equals(d.address, true) } } } catch (_: SecurityException) { null }
    }

    private fun autoConnectBt(): Boolean {
        if (connMode() == "wifi") return false
        if (!hasBtPermission()) { if (prefs.getString("lastAddr", null) != null) ensurePermission(); return false }
        val dev = savedBtDevice() ?: return false
        link.connectBt(dev)
        return true
    }

    // ── Wi-Fi ───────────────────────────────────────────────────────────
    override fun onBeacon(b: WifiSide.Beacon) {
        beacons[b.id] = b
        val savedId = prefs.getString("pcId", null)
        val savedBt = prefs.getString("lastAddr", null)
        val mine = b.id == savedId || (savedBt != null && b.bt.isNotEmpty() && b.bt.equals(savedBt, true))
        if (!mine) return
        if (savedId != b.id || prefs.getString("pcName", "") != b.name) {
            prefs.edit().putString("pcId", b.id).putString("pcName", b.name).apply()
        }
        if (b.bt.isNotEmpty() && prefs.getString("pcBtAuto", null) != b.bt) prefs.edit().putString("pcBtAuto", b.bt).apply()
        if (connMode() == "bt" || connKind == Link.Kind.WIFI || b.connected) { firstReply.remove(b.id); return }

        val pin = prefs.getString("pin_" + b.id, null)
        if (pin == null) {
            if (pinAsked.add(b.id)) askPin(b, L.f("pinFound", b.name))
            return
        }
        val first = firstReply.getOrPut(b.id) { System.currentTimeMillis() }
        if (System.currentTimeMillis() - first > 8000 && pinAsked.add(b.id + "#wrong")) {
            askPin(b, L.t("pinWrong"))   // replies were ignored → probably a wrong PIN
            return
        }
        wifiSide.reply(b, pin)
    }

    override fun onPcConnected(conn: Link.Conn, pcId: String) {
        if (connMode() == "bt") { conn.close(); return }
        firstReply.remove(pcId)
        link.adopt(conn)
    }

    private fun askPin(b: WifiSide.Beacon, message: String, onDone: (() -> Unit)? = null) {
        val input = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            hint = "0000"
            textSize = 24f
            gravity = Gravity.CENTER
            setText(prefs.getString("pin_" + b.id, "") ?: "")
        }
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20f), dp(8f), dp(20f), 0); addView(input) }
        AlertDialog.Builder(this)
            .setTitle("📶 " + L.t("pinTitle") + " — " + b.name)
            .setMessage(message)
            .setView(box)
            .setPositiveButton(L.t("ok")) { _, _ ->
                val pin = input.text.toString().trim()
                if (pin.isNotEmpty()) {
                    prefs.edit().putString("pin_" + b.id, pin).putString("pcId", b.id).putString("pcName", b.name).apply()
                    firstReply.remove(b.id)
                    pinAsked.remove(b.id + "#wrong")
                    onDone?.invoke()
                }
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    @Suppress("MissingPermission")
    private fun chooseDevice() {
        data class Item(val label: String, val action: () -> Unit)
        val items = ArrayList<Item>()
        val now = System.currentTimeMillis()

        // 1) computers found on Wi-Fi (faster, preferred)
        if (connMode() != "bt") {
            beacons.values.filter { now - it.seen < 6000 }.forEach { b ->
                items.add(Item("📶  ${b.name}   · ${L.t("viaWifi")}") {
                    val go = {
                        prefs.edit().putString("pcId", b.id).putString("pcName", b.name).apply()
                        everConnected = false
                        firstReply.remove(b.id)
                        onBeacon(b)
                    }
                    if (prefs.getString("pin_" + b.id, null) == null) askPin(b, L.t("pinMsg"), go) else go()
                })
            }
        }

        // 2) paired Bluetooth devices
        if (connMode() != "wifi") {
            val ad = adapter()
            if (hasBtPermission() && ad != null && ad.isEnabled) {
                val devices = try {
                    ad.bondedDevices.toList().sortedWith(compareBy({ !looksLikePc(it) }, { it.name ?: "" }))
                } catch (_: SecurityException) { emptyList() }
                devices.forEach { d ->
                    items.add(Item((if (looksLikePc(d)) "💻  " else "📱  ") + (d.name ?: d.address) + "   · " + L.t("viaBt")) {
                        prefs.edit().putString("lastAddr", d.address).remove("pcId").apply()
                        everConnected = false
                        link.connectBt(d)
                    })
                }
            } else if (!hasBtPermission()) {
                items.add(Item("ᛒ  " + L.t("btList")) { ensurePermission() })
            } else if (ad != null && !ad.isEnabled) {
                items.add(Item("ᛒ  " + L.t("btOff")) { openBtSettings() })
            }
        }

        if (connMode() != "bt") items.add(Item(L.t("phoneHotspot")) { startPhoneHotspot() })

        if (items.isEmpty()) {
            AlertDialog.Builder(this).setMessage(L.t("noPaired"))
                .setPositiveButton(L.t("openBtSettings")) { _, _ -> openBtSettings() }
                .setNegativeButton(L.t("cancel"), null).show()
            return
        }
        AlertDialog.Builder(this)
            .setTitle(L.t("choosePc"))
            .setItems(items.map { it.label }.toTypedArray()) { _, i -> items[i].action() }
            .setNeutralButton(L.t("openBtSettings")) { _, _ -> openBtSettings() }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    @Suppress("MissingPermission")
    private fun looksLikePc(d: BluetoothDevice): Boolean = try {
        d.bluetoothClass?.majorDeviceClass == android.bluetooth.BluetoothClass.Device.Major.COMPUTER
    } catch (_: SecurityException) { false }

    private fun openBtSettings() {
        try { startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS)) } catch (_: Exception) {}
    }

    // ── Link.Listener ───────────────────────────────────────────────────
    private fun kindLabel(k: Link.Kind) = if (k == Link.Kind.WIFI) "📶 " + L.t("viaWifi") else "ᛒ " + L.t("viaBt")

    override fun onConnecting(name: String, kind: Link.Kind) {
        if (link.isConnected) return
        setStatus(null, L.f("connecting", name))
        btnConnect.text = L.t("disconnect")
        connectedName = name
    }

    override fun onConnected(name: String, kind: Link.Kind) {
        everConnected = true
        connectedName = name
        connKind = kind
        setStatus(true, "$name  ·  " + kindLabel(kind))
        btnConnect.text = L.t("disconnect")
        haptic()
        // bring the PC up to date with everything chosen on the phone
        sendTool()
        sendLaserStyle()
        sendSpotStyle()
        sendLensStyle()
        sendZoomNow()
        sendTimer()
        updateVolumeSession()
        if (pendingShare.isNotEmpty()) { files.sendToPc(ArrayList(pendingShare)); pendingShare.clear() }
        if (mirrorOn) send(JSONObject().put("c", "mirror").put("on", true).put("w", if (kind == Link.Kind.WIFI) 960 else 560))
    }

    override fun onDisconnected(kind: Link.Kind, willRetry: Boolean) {
        if (link.isConnected) return  // another transport already took over
        connKind = null
        pcSpeaker = false
        findViewById<View>(R.id.mediaBar).visibility = View.GONE
        updateVolumeSession()
        if (kind == Link.Kind.WIFI && connectedName.isNotEmpty()) {
            // Wi-Fi dropped: Bluetooth as backup; Wi-Fi comes back by itself when it's reachable again.
            setStatus(null, L.t("reconnecting"))
            main.postDelayed({ if (!link.isConnected && link.bluetoothTarget == null) autoConnectBt() }, 1200)
            return
        }
        if (willRetry) setStatus(null, if (everConnected) L.t("reconnecting") else L.t("failed"))
        else {
            connectedName = ""
            setStatus(false, L.t("notConnected"))
            btnConnect.text = L.t("connect")
        }
    }

    override fun onMessage(msg: JSONObject) {
        when (msg.optString("e")) {
            "state" -> {
                pptOpen = msg.optBoolean("ppt")
                showing = msg.optBoolean("show")
                val newSlide = msg.optInt("slide")
                total = msg.optInt("total")
                screenMode = msg.optString("screen", "normal")
                notes = msg.optString("notes", "")
                val sw = msg.optDouble("sw", 16.0); val sh = msg.optDouble("sh", 9.0)
                if (sh > 0) pad.slideAspect = (sw / sh).toFloat()
                val mi = msg.optInt("mi", -1)
                if (mi != lastMediaIndex) {
                    lastMediaIndex = mi
                    if (pad.zoomScale > 1.001f) resetZoom()
                    if (mi >= 0) {
                        pad.fitBitmap = 1
                        loadMediaThumb(mi)
                    } else {
                        pad.fitBitmap = 0
                        pad.slideBitmap = if (msg.optBoolean("phone")) null else slideThumb
                    }
                }
                if (newSlide != slide) {
                    slide = newSlide
                    notesPanel.scrollTo(0, 0)
                    if (pad.zoomScale > 1.001f) resetZoom()   // new slide starts un-zoomed, like a new photo
                }
                renderState(msg.optInt("click", -1), msg.optInt("clicks", -1))
                onViewState(msg, mi)
            }
            "thumb" -> {
                val bytes = try { Base64.decode(msg.optString("img"), Base64.DEFAULT) } catch (_: Exception) { null }
                val bmp: Bitmap? = bytes?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
                if (msg.optString("which") == "next") imgNext.setImageBitmap(bmp)
                else {
                    imgCur.setImageBitmap(bmp)
                    slideThumb = bmp
                    if (lastMediaIndex < 0) pad.slideBitmap = bmp
                }
            }
            "slides" -> {
                val arr = msg.optJSONArray("titles")
                titles = if (arr == null) emptyList() else List(arr.length()) { arr.optString(it) }
            }
            "ann" -> {
                val arr = msg.optJSONArray("items")
                val list = ArrayList<TouchpadView.PadAnn>()
                if (arr != null) for (i in 0 until arr.length()) {
                    val o = arr.optJSONObject(i) ?: continue
                    val pa = o.optJSONArray("pts")
                    val pts = if (pa == null) null else FloatArray(pa.length()) { pa.optDouble(it).toFloat() }
                    list.add(
                        TouchpadView.PadAnn(
                            o.optString("kind"), o.optDouble("x").toFloat(), o.optDouble("y").toFloat(),
                            parseColor(o.optString("color")), o.optString("text"), o.optInt("size", 28),
                            pts, o.optBoolean("hl")
                        )
                    )
                }
                pad.anns = list
            }
            "frame" -> {
                val bytes = try { Base64.decode(msg.optString("img"), Base64.DEFAULT) } catch (_: Exception) { null }
                val bmp = bytes?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
                val w = msg.optDouble("w", 16.0); val h = msg.optDouble("h", 9.0)
                if (h > 0) pad.mirrorAspect = (w / h).toFloat()
                if (mirrorOn) pad.mirror = bmp
                send(JSONObject().put("c", "frame_ack"))
            }
            "phone_ack" -> Bus.ack()
            "pc_speaker" -> onPcSpeaker(msg)
            "quiz" -> { quizState = msg; if (curView == "quiz") renderViewBar() }
            "picked" -> { lastPicked = msg.optString("name"); haptic(); renderViewBar() }
            "saved" -> toast(L.f("boardSaved", msg.optInt("pages", 1)))
            "fs" -> files.handle(msg)
            "media" -> onMediaState(msg)
            "caret" -> if (mode == Mode.MOUSE && !kbOpen && prefs.getBoolean("autoKb", true)) openKeyboard()
            "new_text" -> textDialog(null, "", msg.optDouble("sx"), msg.optDouble("sy"))
            "edit_text" -> textDialog(msg.optString("id"), msg.optString("text"), 0.0, 0.0)
        }
    }

    private fun parseColor(s: String): Int =
        if (s == "transparent" || s.isBlank()) Color.TRANSPARENT
        else try { Color.parseColor(s) } catch (_: Exception) { Color.RED }

    // ── TouchpadView.Listener ───────────────────────────────────────────
    override fun onRelMove(dx: Float, dy: Float) { pendDx += dx; pendDy += dy; scheduleFlush() }

    override fun onAbs(x: Float, y: Float) {
        pending["abs"] = JSONObject().put("c", "abs").put("x", r4(x)).put("y", r4(y))
        scheduleFlush()
    }

    override fun onButton(button: String, action: String) {
        flushMoves()
        send(JSONObject().put("c", "btn").put("b", button).put("a", action))
    }

    override fun onScroll(steps: Int) { flushMoves(); send(JSONObject().put("c", "scroll").put("d", steps)) }

    override fun onPointer(x: Float, y: Float, phase: String) {
        val kind = when (mode) { Mode.SPOTLIGHT -> "spot"; Mode.LENS -> "lens"; else -> "laser" }
        val o = JSONObject().put("c", kind).put("x", r4(x)).put("y", r4(y)).put("active", phase != "end")
        if (phase == "move") { pending[kind] = o; scheduleFlush() }
        else { pending.remove(kind); flushMoves(); send(o) }
    }

    override fun onZoom(scale: Float, tx: Float, ty: Float) {
        pending["zoom"] = zoomJson()
        scheduleFlush()
        refreshOptionsZoomLabel()
    }

    override fun onScreenPoint(x: Float, y: Float) {
        pending["sabs"] = JSONObject().put("c", "sabs").put("x", r4(x)).put("y", r4(y))
        scheduleFlush()
    }

    override fun onPlace(x: Float, y: Float, phase: String) {
        val kind = if (mode == Mode.TEXT) "text" else "number"
        when (phase) {
            "preview" -> {
                pending["ann_preview"] = JSONObject().put("c", "ann_preview").put("kind", kind)
                    .put("x", r4(x)).put("y", r4(y)).put("color", textBg)
                scheduleFlush()
            }
            "cancel" -> { pending.remove("ann_preview"); flushMoves(); send(JSONObject().put("c", "ann_preview_end")) }
            "commit" -> {
                pending.remove("ann_preview"); flushMoves()
                send(JSONObject().put("c", "ann_tap").put("kind", kind).put("x", r4(x)).put("y", r4(y)).put("size", numberSize))
            }
        }
    }

    override fun onHaptic() = haptic()

    private fun inkHex() = String.format("#%06X", 0xFFFFFF and inkColor)

    override fun onInk(x: Float, y: Float, phase: String) {
        when (phase) {
            "start" -> {
                flushMoves()
                val hl = mode == Mode.HIGHLIGHT
                send(JSONObject().put("c", "ink_start").put("x", r4(x)).put("y", r4(y)).put("color", inkHex())
                    .put("size", if (hl) hlSize else penSize).put("hl", hl))
            }
            "move" -> { inkBuf.add(r4(x)); inkBuf.add(r4(y)); scheduleFlush() }
            "end" -> { flushMoves(); send(JSONObject().put("c", "ink_end")) }
        }
    }

    override fun onErase(x: Float, y: Float, phase: String) {
        if (phase == "end") { pending.remove("ink_erase"); flushMoves(); send(JSONObject().put("c", "ink_erase_end")); return }
        pending["ink_erase"] = JSONObject().put("c", "ink_erase").put("x", r4(x)).put("y", r4(y))
        if (phase == "start") flushMoves() else scheduleFlush()
    }

    private fun zoomJson() = JSONObject().put("c", "zoom").put("s", pad.zoomScale.toDouble())
        .put("x", r4(pad.zoomTx * 100f)).put("y", r4(pad.zoomTy * 100f))

    private fun resetZoom() { pad.setZoom(1f, 0f, 0f); sendZoomNow(); refreshOptionsZoomLabel() }
    private fun sendZoomNow() { pending.remove("zoom"); send(zoomJson()) }

    private fun scheduleFlush() {
        if (!flushScheduled) { flushScheduled = true; main.postDelayed(flushRunnable, 16) }
    }

    private fun flushMoves() {
        main.removeCallbacks(flushRunnable)
        flushScheduled = false
        if (inkBuf.isNotEmpty()) {
            send(JSONObject().put("c", "ink_pts").put("p", JSONArray(inkBuf)))
            inkBuf.clear()
        }
        for (o in pending.values) send(o)
        pending.clear()
        if (pendDx != 0f || pendDy != 0f) {
            send(JSONObject().put("c", "rel").put("dx", r4(pendDx)).put("dy", r4(pendDy)))
            pendDx = 0f; pendDy = 0f
        }
    }

    private fun r4(v: Float): Double = Math.round(v * 10000.0) / 10000.0

    // ── Commands ────────────────────────────────────────────────────────
    private fun send(o: JSONObject) { if (link.isConnected) link.send(o) }

    private fun cmd(c: String, key: String? = null, value: String? = null) {
        flushMoves()
        val o = JSONObject().put("c", c)
        if (key != null) o.put(key, value)
        send(o)
    }

    private fun toolName(m: Mode) = when (m) {
        Mode.MOUSE -> "mouse"; Mode.LASER -> "laser"; Mode.SPOTLIGHT -> "spotlight"; Mode.LENS -> "lens"; Mode.ZOOM -> "zoom"
        Mode.PEN -> "pen"; Mode.HIGHLIGHT -> "highlighter"; Mode.ERASER -> "eraser"
        Mode.NUMBER -> "number"; Mode.TEXT -> "text"
    }

    private fun selectTool(m: Mode) {
        if (mode == Mode.LASER && m != Mode.LASER && laserLabel.isNotEmpty()) {
            laserLabel = ""   // web: the laser caption clears when you change tools
            sendLaserStyle()
        }
        if (m != Mode.MOUSE && mirrorOn) { mirrorOn = false; pad.mirrorOn = false; send(JSONObject().put("c", "mirror").put("on", false)) }
        mode = m
        pad.mode = m
        pad.inkSize = if (m == Mode.HIGHLIGHT) hlSize else penSize
        findViewById<View>(R.id.mouseBar).visibility = if (m == Mode.MOUSE) View.VISIBLE else View.GONE
        if (m != Mode.MOUSE && kbOpen) closeKeyboard()
        toolButtons.forEach { (k, b) -> b.isSelected = k == m }
        padHint.text = hintFor(m)
        buildOptions()
        sendTool()
    }

    private fun hintFor(m: Mode) = when (m) {
        Mode.MOUSE -> if (mirrorOn) L.t("hintMirror") else L.t("hintMouse")
        Mode.LASER -> L.t("hintLaser")
        Mode.SPOTLIGHT -> L.t("hintSpot")
        Mode.LENS -> L.t("hintLens")
        Mode.ZOOM -> L.t("hintZoom")
        Mode.ERASER -> L.t("hintEraser")
        Mode.NUMBER -> L.t("hintNumber")
        Mode.TEXT -> L.t("hintText")
        else -> L.t("hintPen")
    }

    private fun sendTool() {
        send(JSONObject().put("c", "color").put("rgb", String.format("#%06X", 0xFFFFFF and inkColor)))
        send(JSONObject().put("c", "tool").put("t", toolName(mode)))
    }

    private fun sendLaserStyle() {
        send(JSONObject().put("c", "laser_style").put("size", laserSize).put("color", laserColor).put("label", laserLabel))
    }

    private fun sendLensStyle() {
        send(JSONObject().put("c", "lens_style").put("radius", lensRadius).put("zoom", lensZoom))
    }

    private fun sendSpotStyle() {
        send(JSONObject().put("c", "spot_style").put("radius", spotRadius).put("style", spotStyle)
            .put("confetti", spotConfetti).put("color", spotColor ?: ""))
    }

    // ────────────────────────────────────────────────────────────────────
    //  Options panel for the selected tool (mirrors the web app's panels)
    // ────────────────────────────────────────────────────────────────────
    private var zoomLabel: TextView? = null

    private fun dp(v: Float) = (v * resources.displayMetrics.density).toInt()

    private fun buildOptions() {
        optionsPanel.removeAllViews()
        zoomLabel = null
        when (mode) {
            Mode.LASER -> {
                optionsPanel.addView(sizeRow("🔴 " + L.t("size"), 8, 48, 2, laserSize) { laserSize = it; afterStyle(); sendLaserStyle() })
                optionsPanel.addView(swatchRow(L.t("colorL"), laserColors, laserColor) { laserColor = it; afterStyle(); sendLaserStyle(); buildOptions() })
                val row = hRow()
                row.addView(label(L.t("write")))
                val et = EditText(this).apply {
                    setText(laserLabel); hint = L.t("writeHint"); textSize = 14f; setSingleLine()
                    setTextColor(Color.WHITE); setHintTextColor(Color.parseColor("#8A90A2"))
                    layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
                }
                val sender = Runnable { sendLaserStyle() }
                et.addTextChangedListener(object : TextWatcher {
                    override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                    override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                    override fun afterTextChanged(s: Editable?) {
                        laserLabel = s?.toString() ?: ""
                        main.removeCallbacks(sender); main.postDelayed(sender, 200)
                    }
                })
                row.addView(et)
                row.addView(chip("✕", false) { et.setText("") })
                optionsPanel.addView(row)
            }
            Mode.LENS -> {
                optionsPanel.addView(sizeRow("🔎 " + L.t("size"), 60, 400, 10, lensRadius) { lensRadius = it; afterStyle(); sendLensStyle() })
                val zs = listOf(1.5, 2.0, 2.5, 3.0, 4.0).map { z ->
                    chip(String.format(Locale.US, "%.1f×", z), abs(lensZoom - z) < 0.01) { lensZoom = z; afterStyle(); sendLensStyle(); buildOptions() }
                }
                optionsPanel.addView(scrollRow(label(L.t("magnify")), *zs.toTypedArray()))
            }
            Mode.SPOTLIGHT -> {
                optionsPanel.addView(sizeRow("🔦 " + L.t("size"), 60, 400, 10, spotRadius) { spotRadius = it; afterStyle(); sendSpotStyle() })
                val chips = spotStyles.map { (k, t) -> chip(t, spotStyle == k) { spotStyle = k; afterStyle(); sendSpotStyle(); buildOptions() } }
                optionsPanel.addView(scrollRow(*chips.toTypedArray()))
                if (spotStyle == "celebration") {
                    optionsPanel.addView(scrollRow(chip(if (spotConfetti) L.t("confettiOn") else L.t("confettiOff"), spotConfetti) {
                        spotConfetti = !spotConfetti; afterStyle(); sendSpotStyle(); buildOptions()
                    }))
                }
                if (spotStyle == "colorful") {
                    val views = ArrayList<View>()
                    views.add(chip(L.t("colored"), spotColor == null) { spotColor = null; afterStyle(); sendSpotStyle(); buildOptions() })
                    spotColors.forEach { c -> views.add(swatch(c, spotColor == c) { spotColor = c; afterStyle(); sendSpotStyle(); buildOptions() }) }
                    optionsPanel.addView(scrollRow(*views.toTypedArray()))
                }
            }
            Mode.ZOOM -> {
                val lbl = TextView(this).apply {
                    setTextColor(Color.WHITE); typeface = Typeface.MONOSPACE; textSize = 15f
                    gravity = Gravity.CENTER; minWidth = dp(56f)
                }
                zoomLabel = lbl
                optionsPanel.addView(scrollRow(lbl, chip(L.t("reset"), false) { resetZoom() }))
                refreshOptionsZoomLabel()
            }
            Mode.MOUSE -> {
                // what the projector shows — tap to switch between them
                optionsPanel.addView(scrollRow(
                    chip(L.t("backShow"), showing2 == "ppt") { switchTo("ppt") },
                    chip(L.t("desktop"), showing2 == "desktop") { switchTo("desktop") },
                    chip(L.t("media"), showing2 == "media") { switchTo("media") },
                    chip(L.t("phoneOnPc"), Bus.phoneScreenOn) { switchTo("phone") },
                    chip(L.t("camera"), showing2 == "camera") { switchTo("camera") },
                    chip(L.t("board"), showing2 == "board") { switchTo("board") },
                    chip(L.t("docs"), showing2 == "doc") { switchTo("doc") },
                    chip(L.t("gallery"), showing2 == "gallery") { galleryCmd("show") }
                ))
                val soundOn = prefs.getBoolean("phoneAudio", true)
                val extras = ArrayList<View>()
                extras.add(chip(L.t("seeScreen"), mirrorOn) { setMirror(!mirrorOn) })
                extras.add(chip(if (pcSpeaker) L.t("speakerOn") else L.t("speaker"), pcSpeaker) { togglePcSpeaker() })
                extras.add(chip("🔉", false) { phoneVolume(-1) })
                extras.add(chip("🔊", false) { phoneVolume(+1) })
                extras.add(chip(if (soundOn) L.t("sound") else L.t("soundOff"), soundOn) { toggleSound() })
                if (Bus.phoneScreenOn) {
                    val rot = prefs.getInt("phoneRot", 0)
                    val fillOn = prefs.getBoolean("phoneFill", false)
                    extras.add(chip(L.f("rotate", rot), rot != 0) {
                        prefs.edit().putInt("phoneRot", (rot + 90) % 360).apply(); sendPhoneView(); buildOptions()
                    })
                    extras.add(chip(if (fillOn) L.t("fill") else L.t("fit"), fillOn) {
                        prefs.edit().putBoolean("phoneFill", !fillOn).apply(); sendPhoneView(); buildOptions()
                    })
                }
                optionsPanel.addView(scrollRow(*extras.toTypedArray()))
            }
            Mode.PEN, Mode.HIGHLIGHT -> {
                if (mode == Mode.PEN)
                    optionsPanel.addView(sizeRow("✏️ " + L.t("size"), 1, 30, 1, penSize) { penSize = it; pad.inkSize = it; saveToolPrefs() })
                else
                    optionsPanel.addView(sizeRow("🖍 " + L.t("size"), 6, 48, 2, hlSize) { hlSize = it; pad.inkSize = it; saveToolPrefs() })
                val hex = inkColors.map { String.format("#%06X", 0xFFFFFF and it) }
                optionsPanel.addView(swatchRow(L.t("colorL"), hex, String.format("#%06X", 0xFFFFFF and inkColor)) { h ->
                    inkColor = Color.parseColor(h); pad.inkColor = inkColor; saveToolPrefs()
                    send(JSONObject().put("c", "color").put("rgb", h)); buildOptions()
                })
            }
            Mode.NUMBER -> optionsPanel.addView(sizeRow("🔢 " + L.t("size"), 16, 64, 4, numberSize) { numberSize = it; saveToolPrefs() })
            Mode.TEXT -> {
                optionsPanel.addView(scrollRow(
                    chip(L.t("pin"), textStyle == "pin") { textStyle = "pin"; if (textBg == "transparent") textBg = bgColors[0]; saveToolPrefs(); buildOptions() },
                    chip(L.t("plain"), textStyle == "plain") { textStyle = "plain"; saveToolPrefs(); buildOptions() },
                    chip(L.t("box"), textStyle == "box") { textStyle = "box"; if (textBg == "transparent") textBg = bgColors[0]; saveToolPrefs(); buildOptions() }
                ))
                optionsPanel.addView(sizeRow(L.t("size"), 10, 32, 2, textFont) { textFont = it; saveToolPrefs() })
                optionsPanel.addView(swatchRow(L.t("fontColor"), textColors, textColor) { textColor = it; saveToolPrefs(); buildOptions() })
                val bgViews = ArrayList<View>()
                bgViews.add(label(L.t("background")))
                bgColors.forEach { c -> bgViews.add(swatch(c, textBg == c) { textBg = c; saveToolPrefs(); buildOptions() }) }
                if (textStyle == "plain") bgViews.add(chip(L.t("noBg"), textBg == "transparent") {
                    textBg = if (textBg == "transparent") bgColors[0] else "transparent"; saveToolPrefs(); buildOptions()
                })
                optionsPanel.addView(scrollRow(*bgViews.toTypedArray()))
            }
            else -> {}
        }
        optionsPanel.visibility = if (optionsPanel.childCount > 0) View.VISIBLE else View.GONE
    }

    private fun afterStyle() { saveToolPrefs(); syncPad() }

    private fun refreshOptionsZoomLabel() {
        zoomLabel?.text = String.format(Locale.US, "%.1f×", pad.zoomScale)
    }

    /** Mouse → "See the screen": stream the PC screen onto the touchpad. */
    private fun setMirror(on: Boolean) {
        mirrorOn = on
        pad.mirrorOn = on
        padHint.text = hintFor(mode)
        send(JSONObject().put("c", "mirror").put("on", on).put("w", if (connKind == Link.Kind.WIFI) 960 else 560))
        buildOptions()
    }

    private fun hRow() = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            .apply { topMargin = dp(2f); bottomMargin = dp(2f) }
    }

    private fun scrollRow(vararg views: View): View {
        val inner = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        views.forEach { inner.addView(it) }
        return HorizontalScrollView(this).apply {
            isHorizontalScrollBarEnabled = false
            addView(inner)
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
                .apply { topMargin = dp(2f); bottomMargin = dp(2f) }
        }
    }

    private fun label(t: String) = TextView(this).apply {
        text = t; textSize = 12f; setTextColor(Color.parseColor("#8A90A2"))
        setPadding(0, 0, dp(8f), 0)
    }

    private fun chip(t: String, selected: Boolean, onClick: () -> Unit) = Button(this).apply {
        text = t; isAllCaps = false; textSize = 12f; setTextColor(Color.WHITE)
        setBackgroundResource(R.drawable.btn_tool); stateListAnimator = null
        minWidth = 0; minHeight = 0; minimumWidth = 0; minimumHeight = 0
        setPadding(dp(12f), 0, dp(12f), 0)
        isSelected = selected
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(34f)).apply { marginEnd = dp(6f) }
        setOnClickListener { onClick() }
    }

    private fun swatch(hex: String, selected: Boolean, onClick: () -> Unit) = View(this).apply {
        val c = parseColor(hex)
        background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL; setColor(c)
            setStroke(dp(if (selected) 3f else 1f), if (selected) Color.parseColor("#4F8CFF") else Color.parseColor("#555A6B"))
        }
        layoutParams = LinearLayout.LayoutParams(dp(30f), dp(30f)).apply { marginEnd = dp(8f) }
        setOnClickListener { onClick() }
    }

    private fun swatchRow(title: String, colors: List<String>, selected: String, onPick: (String) -> Unit): View {
        val views = ArrayList<View>()
        views.add(label(title))
        colors.forEach { c -> views.add(swatch(c, c.equals(selected, ignoreCase = true)) { onPick(c) }) }
        return scrollRow(*views.toTypedArray())
    }

    private fun sizeRow(title: String, min: Int, max: Int, step: Int, value: Int, onChange: (Int) -> Unit): View {
        val row = hRow()
        val valueText = TextView(this).apply {
            text = "${value}px"; textSize = 12f; setTextColor(Color.WHITE); typeface = Typeface.MONOSPACE
            minWidth = dp(44f); gravity = Gravity.END
        }
        val seek = SeekBar(this).apply {
            this.max = (max - min) / step
            progress = ((value - min) / step).coerceIn(0, this.max)
            layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(sb: SeekBar?, p: Int, fromUser: Boolean) {
                    val v = min + p * step
                    valueText.text = "${v}px"
                    if (fromUser) onChange(v)
                }
                override fun onStartTrackingTouch(sb: SeekBar?) {}
                override fun onStopTrackingTouch(sb: SeekBar?) {}
            })
        }
        row.addView(label(title))
        row.addView(chip("−", false) { seek.progress = maxOf(0, seek.progress - 2); onChange(min + seek.progress * step) })
        row.addView(seek)
        row.addView(chip("+", false) { seek.progress = minOf(seek.max, seek.progress + 2); onChange(min + seek.progress * step) })
        row.addView(valueText)
        return row
    }

    // ── Phone screen → PC (Android asks for permission every time) ─────
    private fun togglePhoneScreen() {
        if (Bus.phoneScreenOn) { stopService(Intent(this, ProjectionService::class.java)); return }
        if (!link.isConnected) { toast(L.t("notConnected")); return }
        if (prefs.getBoolean("phoneAudio", true) && Build.VERSION.SDK_INT >= 29 && !hasAudioPermission()) {
            pendingScreenAfterPerm = true
            toast(L.t("soundPerm"))
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 44)
            return
        }
        val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        @Suppress("DEPRECATION")
        startActivityForResult(mpm.createScreenCaptureIntent(), 77)
    }

    @Deprecated("uses the platform result API")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == 81 && resultCode == RESULT_OK && data?.data != null) {
            val u = data.data!!
            showing2 = "doc"
            val go = { files.sendToPc(listOf(u), present = true, kind = "doc"); toast(L.t("docPreparing")) }
            if (connKind == Link.Kind.BT) {
                Thread {
                    val mb = (files.totalSize(listOf(u)) / (1024 * 1024)).toInt()
                    main.post {
                        if (mb >= 15) AlertDialog.Builder(this).setMessage(L.f("mediaBt", mb))
                            .setPositiveButton(L.t("yes")) { _, _ -> go() }.setNegativeButton(L.t("cancel"), null).show()
                        else go()
                    }
                }.start()
            } else go()
            buildOptions()
            return
        }
        if (requestCode == 79 && resultCode == RESULT_OK && data != null) {
            val uris = ArrayList<android.net.Uri>()
            data.data?.let { uris.add(it) }
            data.clipData?.let { c -> for (k in 0 until c.itemCount) uris.add(c.getItemAt(k).uri) }
            if (uris.isNotEmpty()) startMedia(uris.distinct())
            return
        }
        if (requestCode == 78 && resultCode == RESULT_OK && data != null) {
            val uris = ArrayList<android.net.Uri>()
            data.data?.let { uris.add(it) }
            data.clipData?.let { c -> for (k in 0 until c.itemCount) uris.add(c.getItemAt(k).uri) }
            if (uris.isNotEmpty()) files.sendToPc(uris.distinct())
            return
        }
        if (requestCode != 77 || resultCode != RESULT_OK || data == null) return
        val i = Intent(this, ProjectionService::class.java)
            .putExtra(ProjectionService.EXTRA_CODE, resultCode)
            .putExtra(ProjectionService.EXTRA_DATA, data)
            .putExtra(ProjectionService.EXTRA_WIDTH, if (connKind == Link.Kind.WIFI) 900 else 520)
            .putExtra(ProjectionService.EXTRA_WIFI, connKind == Link.Kind.WIFI)
            .putExtra(ProjectionService.EXTRA_AUDIO, prefs.getBoolean("phoneAudio", true) && hasAudioPermission() && !pcSpeaker)
        startForegroundService(i)
    }

    private var pendingScreenAfterPerm = false

    private fun hasAudioPermission() = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    private fun sendPhoneView() {
        send(JSONObject().put("c", "phone_view").put("rot", prefs.getInt("phoneRot", 0)).put("fill", prefs.getBoolean("phoneFill", false)))
    }

    private fun toggleSound() {
        val on = !prefs.getBoolean("phoneAudio", true)
        if (on && Build.VERSION.SDK_INT < 29) { toast(L.t("soundNeeds10")); return }
        prefs.edit().putBoolean("phoneAudio", on).apply()
        if (on && !hasAudioPermission()) {
            toast(L.t("soundPerm"))
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 44)
        } else if (Bus.phoneScreenOn) Bus.setAudio?.invoke(on)
        buildOptions()
    }

    private fun loadMediaThumb(index: Int) {
        val uri = mediaUris.getOrNull(index)
        if (uri == null) { pad.slideBitmap = null; return }
        Thread {
            val bmp: Bitmap? = try {
                if (Build.VERSION.SDK_INT >= 29) contentResolver.loadThumbnail(uri, android.util.Size(720, 720), null)
                else {
                    val type = contentResolver.getType(uri) ?: ""
                    if (type.startsWith("video/")) {
                        val r = android.media.MediaMetadataRetriever()
                        r.setDataSource(this, uri)
                        val f = r.frameAtTime
                        r.release(); f
                    } else contentResolver.openInputStream(uri)?.use { s ->
                        BitmapFactory.decodeStream(s, null, BitmapFactory.Options().apply { inSampleSize = 4 })
                    }
                }
            } catch (_: Exception) { null }
            main.post { if (lastMediaIndex == index) pad.slideBitmap = bmp }
        }.start()
    }

    // ── Photos & videos full-screen on the projector ───────────────────
    /** what the projector shows now: ppt, desktop, media, phone (highlights the Mouse-mode button) */
    private var showing2 = "ppt"
    private var mediaCount = 0
    private lateinit var mediaRow1: LinearLayout
    private lateinit var mediaRow2: LinearLayout
    private var mediaSeeking = false
    private var mediaDur = 0.0

    // ════════════════════════════════════════════════════════════════════
    //  🧑‍🏫 Whiteboard · 📑 Documents · 🗳️ Quiz · 🎲 Picker — the control bar
    // ════════════════════════════════════════════════════════════════════
    private var curView = ""
    private var curBg = ""
    private var viewPage = 0
    private var viewPages = 0
    private var docName = ""
    private var quizState: JSONObject? = null
    private var lastPicked = ""
    private var pickRemaining = -1
    private var lastPickList = ""
    private val pickPools = HashMap<String, MutableList<String>>()

    private fun boardColor(bg: String) = when (bg) {
        "black" -> Color.parseColor("#16181d"); "green" -> Color.parseColor("#1f4d3a"); else -> Color.WHITE
    }

    private fun solidBitmap(c: Int): Bitmap = Bitmap.createBitmap(16, 9, Bitmap.Config.ARGB_8888).apply { eraseColor(c) }

    /** The PC told us what it is showing (whiteboard, document, quiz, picker, phone screen / camera…). */
    private fun onViewState(msg: JSONObject, mi: Int) {
        val view = msg.optString("view", "")
        val bg = msg.optString("bg", "")
        viewPage = msg.optInt("page"); viewPages = msg.optInt("pages"); docName = msg.optString("doc")
        if (view != curView || bg != curBg) {
            curView = view; curBg = bg
            when (view) {
                "board" -> { pad.fitBitmap = 0; pad.slideBitmap = solidBitmap(boardColor(bg)); showing2 = "board" }
                "doc" -> { pad.fitBitmap = 0; showing2 = "doc" }
                "quiz" -> { if (mi < 0) pad.slideBitmap = null; showing2 = "quiz" }
                "gallery" -> { if (mi < 0) pad.slideBitmap = null; showing2 = "gallery"; galSingle = msg.optBoolean("single", true) }
                "picker" -> { if (mi < 0) pad.slideBitmap = null; showing2 = "picker" }
                "" -> {
                    if (mi < 0) { pad.fitBitmap = 0; pad.slideBitmap = slideThumb }
                    if (showing2 in listOf("board", "doc", "quiz", "picker", "camera", "gallery")) showing2 = "ppt"
                }
            }
            if (mode == Mode.MOUSE) buildOptions()
        }
        renderViewBar()
    }

    private fun renderViewBar() {
        val bar = findViewById<LinearLayout>(R.id.viewBar)
        bar.removeAllViews()
        when (curView) {
            "board" -> {
                slideInfo.text = "🧑‍🏫 " + L.f("pageOf", viewPage, viewPages)
                bar.addView(scrollRow(
                    chip("◀", false) { haptic(); cmd("prev") },
                    chip(if (viewPage >= viewPages) L.t("newPage") else "▶", false) { haptic(); cmd("next") },
                    chip("💾 " + L.t("save"), false) { send(JSONObject().put("c", "board").put("a", "save")) },
                    chip(L.t("mediaClose"), false) { send(JSONObject().put("c", "board").put("a", "close")) }
                ))
                val bgs = listOf("white" to L.t("bgWhite"), "grid" to L.t("bgGrid"), "black" to L.t("bgBlack"), "green" to L.t("bgGreen"))
                bar.addView(scrollRow(*bgs.map { (k, t) ->
                    chip(t, curBg == k) {
                        prefs.edit().putString("boardBg", k).apply()
                        send(JSONObject().put("c", "board").put("a", "bg").put("v", k))
                    }
                }.toTypedArray()))
            }
            "doc" -> {
                slideInfo.text = "📑 " + L.f("pageOf", viewPage, viewPages)
                bar.addView(scrollRow(
                    chip("◀", false) { haptic(); cmd("prev") },
                    chip("▶", false) { haptic(); cmd("next") },
                    label("  " + docName),
                    chip(L.t("mediaClose"), false) { send(JSONObject().put("c", "doc").put("a", "close")) }
                ))
            }
            "quiz" -> {
                val q = quizState
                val n = q?.optInt("n", 4) ?: 4
                val counts = q?.optJSONArray("counts")
                val parts = (0 until n).joinToString("  ") { i -> "${'A' + i} ${counts?.optInt(i) ?: 0}" }
                val qi = q?.optInt("qi", 0) ?: 0; val qn = q?.optInt("qn", 0) ?: 0
                bar.addView(TextView(this).apply {
                    val left = q?.optInt("left", -1) ?: -1
                    text = (if (qn > 1) "($qi/$qn)  " else "") + (if (left >= 0) "⏱ $left   ·   " else "") + L.f("quizAnswers", q?.optInt("total") ?: 0) +
                            "  ·  👥 " + (q?.optInt("players") ?: 0) + "   ·   " + parts
                    setTextColor(Color.WHITE); textSize = 14f; setPadding(dp(4f), 0, 0, dp(4f))
                })
                val row = ArrayList<View>()
                if (q?.optBoolean("reveal") != true) row.add(chip(L.t("quizShowResults"), false) { quizCmd("reveal") })
                else if (runIndex + 1 < (runQuiz?.length() ?: 0))
                    row.add(chip(L.t("quizNextQ"), false) { sendQuestion(runIndex + 1) })
                row.add(label(L.t("quizCorrect")))
                for (i in 0 until n) row.add(chip("${'A' + i}", q?.optInt("correct", -1) == i) { quizCmd("correct", i) })
                bar.addView(scrollRow(*row.toTypedArray()))
                bar.addView(scrollRow(
                    chip(L.t("quizScores"), false) { quizCmd("scores") },
                    chip(L.t("quizNew"), false) { quizDialog() },
                    chip(L.t("quizReset"), false) {
                        AlertDialog.Builder(this).setMessage(L.t("quizResetAsk"))
                            .setPositiveButton(L.t("ok")) { _, _ -> quizCmd("reset") }
                            .setNegativeButton(L.t("cancel"), null).show()
                    },
                    chip(L.t("mediaClose"), false) { quizCmd("close") }
                ))
            }
            "gallery" -> {
                slideInfo.text = "📸 " + L.f("pageOf", viewPage, viewPages)
                bar.addView(scrollRow(
                    chip("◀", false) { haptic(); cmd("prev") },
                    chip("▶", false) { haptic(); cmd("next") },
                    chip(L.t("galGrid"), !galSingle) { galSingle = false; galleryCmd("grid") },
                    chip(L.t("galOne"), galSingle) { galSingle = true; galleryCmd("single") },
                    chip(L.t("galClear"), false) {
                        AlertDialog.Builder(this).setMessage(L.t("galClearAsk"))
                            .setPositiveButton(L.t("ok")) { _, _ -> galleryCmd("clear") }
                            .setNegativeButton(L.t("cancel"), null).show()
                    },
                    chip(L.t("mediaClose"), false) { galleryCmd("close") }
                ))
            }
            "picker" -> {
                val who = if (lastPicked.isEmpty()) "🎲 …" else "🎲 $lastPicked"
                val left = if (pickRemaining >= 0) "   ·   " + L.f("pickLeft", pickRemaining) else ""
                bar.addView(TextView(this).apply {
                    text = who + left; setTextColor(Color.WHITE); textSize = 16f; setTypeface(typeface, Typeface.BOLD)
                    setPadding(dp(4f), 0, 0, dp(4f))
                })
                bar.addView(scrollRow(
                    chip(L.t("pickAgain"), false) { doPick(lastPickList) },
                    chip(L.t("pickWheelShort"), prefs.getString("pickStyle", "names") == "wheel") {
                        val w = prefs.getString("pickStyle", "names") != "wheel"
                        prefs.edit().putString("pickStyle", if (w) "wheel" else "names").apply()
                        renderViewBar()
                    },
                    chip(L.t("pickLists"), false) { pickerDialog() },
                    chip(L.t("mediaClose"), false) { send(JSONObject().put("c", "picker").put("a", "close")) }
                ))
            }
            else -> { bar.visibility = View.GONE; return }
        }
        bar.visibility = View.VISIBLE
    }

    private var galSingle = true
    private fun galleryCmd(a: String) { haptic(); send(JSONObject().put("c", "gallery").put("a", a)) }

    // ── 📑 documents ──
    private fun pickDoc() {
        if (!link.isConnected) { toast(L.t("notConnected")); return }
        val i = Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
            .putExtra(Intent.EXTRA_MIME_TYPES, arrayOf(
                "application/pdf", "application/msword",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "application/vnd.ms-powerpoint",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                "application/vnd.openxmlformats-officedocument.presentationml.slideshow",
                "application/rtf", "text/plain", "application/vnd.oasis.opendocument.text",
                "application/vnd.oasis.opendocument.presentation"))
        @Suppress("DEPRECATION")
        startActivityForResult(i, 81)
    }

    // ── 🗳️ quiz: saved quizzes, written options, speed scores ──────────
    private var runQuiz: org.json.JSONArray? = null     // the quiz being played (list of questions)
    private var runIndex = 0
    private var runName = ""

    private fun quizzes(): JSONObject = try { JSONObject(prefs.getString("quizzes", "{}") ?: "{}") } catch (_: Exception) { JSONObject() }
    private fun saveQuizzes(o: JSONObject) = prefs.edit().putString("quizzes", o.toString()).apply()

    private fun quizCmd(a: String, v: Int = 0) {
        haptic()
        send(JSONObject().put("c", "quiz").put("a", a).put("v", v))
    }

    /** Sends question number [i] of the quiz being played. */
    private fun sendQuestion(i: Int) {
        val quiz = runQuiz ?: return
        if (i < 0 || i >= quiz.length()) return
        runIndex = i
        val q = quiz.optJSONObject(i) ?: return
        val opts = q.optJSONArray("opts") ?: org.json.JSONArray()
        send(JSONObject().put("c", "quiz").put("a", "start")
            .put("q", q.optString("q")).put("opts", opts).put("n", maxOf(2, opts.length()))
            .put("correct", q.optInt("correct", -1)).put("secs", q.optInt("secs", 0))
            .put("qi", i + 1).put("qn", quiz.length()))
        showing2 = "quiz"
        buildOptions()
    }

    /** 🗳️ The list of saved quizzes: play ▶, edit ✏️ or delete 🗑. */
    private fun quizDialog() {
        if (!link.isConnected) { toast(L.t("notConnected")); return }
        val all = quizzes()
        val names = all.keys().asSequence().toList()
        val items = names.map { "▶  $it  (${all.optJSONArray(it)?.length() ?: 0})" } + listOf(L.t("quizNewQuiz"), L.t("quizQuick"))
        AlertDialog.Builder(this)
            .setTitle(L.t("quizTitle"))
            .setItems(items.toTypedArray()) { _, i ->
                when {
                    i < names.size -> quizActions(names[i])
                    i == names.size -> quizEditor(null)
                    else -> quickQuestion()
                }
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    private fun quizActions(name: String) {
        AlertDialog.Builder(this)
            .setTitle(name)
            .setItems(arrayOf(L.t("quizPlay"), L.t("quizEdit"), L.t("quizDelete"))) { _, i ->
                when (i) {
                    0 -> { runQuiz = quizzes().optJSONArray(name); runName = name; sendQuestion(0) }
                    1 -> quizEditor(name)
                    else -> AlertDialog.Builder(this).setMessage(L.f("quizDeleteAsk", name))
                        .setPositiveButton(L.t("quizDelete")) { _, _ ->
                            val all = quizzes(); all.remove(name); saveQuizzes(all); toast("🗑 $name")
                        }.setNegativeButton(L.t("cancel"), null).show()
                }
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    /** Add / edit a quiz: its name and its questions. */
    private fun quizEditor(existing: String?) {
        val all = quizzes()
        var name = existing ?: ""
        val questions = if (existing != null) all.optJSONArray(existing) ?: org.json.JSONArray() else org.json.JSONArray()
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20f), dp(8f), dp(20f), 0) }
        val nameEt = EditText(this).apply { hint = L.t("quizName"); setText(name) }
        val list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        box.addView(nameEt); box.addView(list)
        fun refresh() {
            list.removeAllViews()
            for (i in 0 until questions.length()) {
                val q = questions.optJSONObject(i) ?: continue
                val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
                row.addView(TextView(this).apply {
                    text = "${i + 1}. " + q.optString("q").ifBlank { "—" }
                    layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
                    maxLines = 2; textSize = 14f
                })
                row.addView(chip("✏️", false) { questionEditor(questions, i) { refresh() } })
                row.addView(chip("🗑", false) {
                    val out = org.json.JSONArray()
                    for (k in 0 until questions.length()) if (k != i) out.put(questions.opt(k))
                    while (questions.length() > 0) questions.remove(questions.length() - 1)
                    for (k in 0 until out.length()) questions.put(out.opt(k))
                    refresh()
                })
                list.addView(row)
            }
            list.addView(chip(L.t("quizAddQ"), false) { questionEditor(questions, -1) { refresh() } })
        }
        refresh()
        AlertDialog.Builder(this)
            .setTitle(existing ?: L.t("quizNewQuiz"))
            .setView(ScrollView(this).apply { addView(box) })
            .setPositiveButton(L.t("save")) { _, _ ->
                name = nameEt.text.toString().trim().ifBlank { existing ?: L.t("quizUntitled") }
                val store = quizzes()
                if (existing != null && existing != name) store.remove(existing)
                store.put(name, questions)
                saveQuizzes(store)
                toast("✓ $name")
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    /**
     * Rows "A [answer text] (•)" for 2–6 answers. Each view has exactly ONE parent
     * (the old version put the tick button in two places, which made Android close the app).
     */
    private class AnswerRows(val box: LinearLayout, val texts: List<EditText>, val ticks: List<RadioButton>, val rows: List<View>) {
        var correct = -1
            private set
        fun select(i: Int) { correct = i; ticks.forEachIndexed { k, rb -> rb.isChecked = k == i } }
        fun show(count: Int) { rows.forEachIndexed { k, r -> r.visibility = if (k < count) View.VISIBLE else View.GONE } }
    }

    private fun answerRows(initial: org.json.JSONArray?, correct: Int, withTicks: Boolean): AnswerRows {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val texts = ArrayList<EditText>(); val ticks = ArrayList<RadioButton>(); val rows = ArrayList<View>()
        var holder: AnswerRows? = null
        for (i in 0 until 6) {
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
            row.addView(TextView(this).apply {
                text = "${'A' + i}"; textSize = 16f; setTypeface(typeface, Typeface.BOLD)
                setPadding(0, 0, dp(10f), 0)
            })
            val et = EditText(this).apply {
                hint = if (i < 2) L.t("quizAnswerHint") else L.t("quizAnswerOpt")
                setText(initial?.optString(i, "") ?: "")
                setSingleLine()
                layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            }
            row.addView(et)
            texts.add(et)
            val rb = RadioButton(this)
            if (withTicks) {
                rb.setOnClickListener { holder?.select(i) }
                row.addView(rb)          // the tick lives only in this row
            }
            ticks.add(rb)
            box.addView(row)
            rows.add(row)
        }
        holder = AnswerRows(box, texts, ticks, rows)
        holder.select(if (withTicks) correct.coerceIn(0, 5) else -1)
        return holder
    }

    /** One question: its text, 2–6 written answers and which one is right. */
    private fun questionEditor(questions: org.json.JSONArray, index: Int, onDone: () -> Unit) {
        val q = if (index >= 0) questions.optJSONObject(index) ?: JSONObject() else JSONObject()
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20f), dp(8f), dp(20f), 0) }
        val qEt = EditText(this).apply { hint = L.t("quizQuestionHint"); setText(q.optString("q")); maxLines = 3 }
        box.addView(qEt)
        box.addView(TextView(this).apply {
            text = L.t("quizOptionsHint"); textSize = 12f; setTextColor(Color.parseColor("#8A90A2")); setPadding(0, dp(8f), 0, dp(4f))
        })
        val rows = answerRows(q.optJSONArray("opts"), q.optInt("correct", 0), withTicks = true)
        box.addView(rows.box)
        var secs = q.optInt("secs", 0)
        box.addView(TextView(this).apply { text = L.t("quizTime"); textSize = 12f; setTextColor(Color.parseColor("#8A90A2")); setPadding(0, dp(10f), 0, dp(4f)) })
        val timeRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        fun fillTime() {
            timeRow.removeAllViews()
            listOf(0, 10, 20, 30, 60).forEach { v ->
                timeRow.addView(chip(if (v == 0) L.t("quizNoTime") else "${'$'}{v}s", secs == v) { secs = v; fillTime() })
            }
        }
        fillTime()
        box.addView(HorizontalScrollView(this).apply { isHorizontalScrollBarEnabled = false; addView(timeRow) })
        AlertDialog.Builder(this)
            .setTitle(L.t("quizQuestion"))
            .setView(ScrollView(this).apply { addView(box) })
            .setPositiveButton(L.t("save")) { _, _ ->
                val used = QuizText.trimAnswers(rows.texts.map { it.text.toString() })
                val arr = org.json.JSONArray(); used.forEach { arr.put(it) }
                val out = JSONObject().put("q", qEt.text.toString().trim()).put("opts", arr)
                    .put("correct", rows.correct.coerceIn(0, used.size - 1)).put("secs", secs)
                if (index >= 0) questions.put(index, out) else questions.put(out)
                onDone()
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    /** ⚡ One quick question — A, B, C… and (if you like) a few words next to each. */
    private fun quickQuestion() {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20f), dp(8f), dp(20f), 0) }
        val q = EditText(this).apply { hint = L.t("quizQuestionHint"); setText(prefs.getString("quizLastQ", "")) }
        val lbl = TextView(this).apply { text = L.t("quizHowMany"); setPadding(0, dp(12f), 0, dp(4f)) }
        val rg = RadioGroup(this).apply { orientation = RadioGroup.HORIZONTAL }
        val ids = HashMap<Int, Int>()
        for (n in 2..6) {
            val rb = RadioButton(this).apply { text = if (n == 2) "A–B" else "A–${'A' + n - 1}"; id = View.generateViewId() }
            ids[rb.id] = n; rg.addView(rb)
        }
        val saved = try { org.json.JSONArray(prefs.getString("quizLastOpts", "[]")) } catch (_: Exception) { org.json.JSONArray() }
        val rows = answerRows(saved, -1, withTicks = false)
        var qSecs = prefs.getInt("quizLastSecs", 0)
        val timeLbl = TextView(this).apply { text = L.t("quizTime"); textSize = 12f; setTextColor(Color.parseColor("#8A90A2")); setPadding(0, dp(10f), 0, dp(4f)) }
        val timeRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        fun fillTime() {
            timeRow.removeAllViews()
            listOf(0, 10, 20, 30, 60).forEach { v ->
                timeRow.addView(chip(if (v == 0) L.t("quizNoTime") else "${'$'}{v}s", qSecs == v) { qSecs = v; fillTime() })
            }
        }
        fillTime()
        val words = TextView(this).apply {
            text = L.t("quizWordsHint"); textSize = 12f; setTextColor(Color.parseColor("#8A90A2")); setPadding(0, dp(10f), 0, dp(2f))
        }
        rg.setOnCheckedChangeListener { _, id -> rows.show(ids[id] ?: 4) }
        val lastN = prefs.getInt("quizLastN", 4)
        ids.entries.firstOrNull { it.value == lastN }?.let { rg.check(it.key) }
        rows.show(lastN)
        box.addView(q); box.addView(lbl); box.addView(rg)
        box.addView(timeLbl); box.addView(HorizontalScrollView(this).apply { isHorizontalScrollBarEnabled = false; addView(timeRow) })
        box.addView(words); box.addView(rows.box)
        box.addView(TextView(this).apply { text = L.t("quizNote"); textSize = 12f; setTextColor(Color.parseColor("#8A90A2")); setPadding(0, dp(10f), 0, 0) })
        AlertDialog.Builder(this)
            .setTitle(L.t("quizQuick"))
            .setView(ScrollView(this).apply { addView(box) })
            .setPositiveButton(L.t("quizStart")) { _, _ ->
                val n = ids[rg.checkedRadioButtonId] ?: 4
                val opts = QuizText.quickAnswers(rows.texts.map { it.text.toString() }, n)
                val arr = org.json.JSONArray(); opts.forEach { arr.put(it) }
                prefs.edit().putString("quizLastQ", q.text.toString()).putInt("quizLastN", n)
                    .putString("quizLastOpts", arr.toString()).putInt("quizLastSecs", qSecs).apply()
                val one = JSONObject().put("q", q.text.toString().trim()).put("opts", arr).put("correct", -1).put("secs", qSecs)
                runQuiz = org.json.JSONArray().put(one); runName = ""; runIndex = 0
                send(JSONObject().put("c", "quiz").put("a", "start").put("q", q.text.toString().trim())
                    .put("opts", arr).put("n", n).put("correct", -1).put("qi", 1).put("qn", 1).put("secs", qSecs))
                showing2 = "quiz"
                buildOptions()
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }


    // ── 🎲 random picker ──
    private fun pickLists(): JSONObject = try { JSONObject(prefs.getString("pickLists", "{}") ?: "{}") } catch (_: Exception) { JSONObject() }

    /** "1-30" (or "1–30") → 1, 2 … 30; otherwise one name per line. */
    private fun parseNames(text: String): List<String> = PickerNames.parse(text)

    private fun pickerDialog() {
        if (!link.isConnected) { toast(L.t("notConnected")); return }
        val lists = pickLists()
        var current = prefs.getString("pickList", "") ?: ""
        if (current.isEmpty() || !lists.has(current)) current = lists.keys().asSequence().firstOrNull() ?: L.t("pickDefaultList")
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20f), dp(8f), dp(20f), 0) }
        val listBtn = Button(this).apply { isAllCaps = false; text = "📋 $current  ▾" }
        val names = EditText(this).apply {
            hint = L.t("pickNamesHint"); minLines = 5; maxLines = 10; gravity = Gravity.TOP or Gravity.START
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_CAP_WORDS
            setText(lists.optString(current, ""))
        }
        val noRepeat = CheckBox(this).apply { text = L.t("pickNoRepeat"); isChecked = prefs.getBoolean("pickNoRepeat", true) }
        val wheel = CheckBox(this).apply { text = L.t("pickWheel"); isChecked = prefs.getString("pickStyle", "names") == "wheel" }
        box.addView(listBtn); box.addView(names); box.addView(noRepeat); box.addView(wheel)
        listBtn.setOnClickListener {
            val keys = pickLists().keys().asSequence().toList()
            val items = keys + L.t("pickNewList")
            AlertDialog.Builder(this).setTitle(L.t("pickLists")).setItems(items.toTypedArray()) { _, i ->
                if (i < keys.size) { current = keys[i]; names.setText(pickLists().optString(current, "")); listBtn.text = "📋 $current  ▾" }
                else {
                    val et = EditText(this).apply { hint = L.t("pickListName") }
                    AlertDialog.Builder(this).setTitle(L.t("pickNewList")).setView(et)
                        .setPositiveButton(L.t("ok")) { _, _ ->
                            val n = et.text.toString().trim()
                            if (n.isNotEmpty()) { current = n; names.setText(""); listBtn.text = "📋 $current  ▾" }
                        }.setNegativeButton(L.t("cancel"), null).show()
                }
            }.show()
        }
        fun save() {
            val l = pickLists().put(current, names.text.toString())
            prefs.edit().putString("pickLists", l.toString()).putString("pickList", current)
                .putBoolean("pickNoRepeat", noRepeat.isChecked)
                .putString("pickStyle", if (wheel.isChecked) "wheel" else "names").apply()
            pickPools.remove(current)   // list edited → start a fresh round
        }
        AlertDialog.Builder(this)
            .setTitle(L.t("pickTitle"))
            .setView(ScrollView(this).apply { addView(box) })
            .setPositiveButton(L.t("pickBtn")) { _, _ -> save(); doPick(current) }
            .setNeutralButton(L.t("save")) { _, _ -> save(); toast("✓") }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    private fun doPick(listName: String) {
        val all = parseNames(pickLists().optString(listName, ""))
        if (all.isEmpty()) { toast(L.t("pickEmpty")); pickerDialog(); return }
        lastPickList = listName
        val noRepeat = prefs.getBoolean("pickNoRepeat", true)
        val pool = pickPools.getOrPut(listName) { mutableListOf() }
        val (winner, left) = PickerNames.pick(all, pool, noRepeat, java.security.SecureRandom())
        pickRemaining = left
        lastPicked = ""
        haptic()
        send(JSONObject().put("c", "picker").put("a", "pick").put("names", org.json.JSONArray(all))
            .put("winner", all.indexOf(winner)).put("title", listName).put("remaining", pickRemaining)
            .put("style", prefs.getString("pickStyle", "names")))
        showing2 = "picker"
        renderViewBar()
    }

    /** One place that switches the projector between PowerPoint, desktop, photos/videos and the phone screen. */
    private fun switchTo(target: String) {
        haptic()
        if (!link.isConnected) { toast(L.t("notConnected")); return }
        when (target) {
            "ppt" -> {
                stopPhoneScreen()
                send(JSONObject().put("c", "media").put("a", "hide"))
                cmd("back_show")
                showing2 = "ppt"
            }
            "desktop" -> {
                stopPhoneScreen()
                cmd("desktop")
                showing2 = "desktop"
            }
            "media" -> {
                stopPhoneScreen()
                if (mediaCount > 0) {
                    AlertDialog.Builder(this)
                        .setTitle(L.t("mediaTitle"))
                        .setItems(arrayOf(L.t("mediaAgain"), L.t("mediaNew"))) { _, i ->
                            if (i == 0) { send(JSONObject().put("c", "media").put("a", "show")); showing2 = "media"; buildOptions() }
                            else pickMedia()
                        }
                        .setNegativeButton(L.t("cancel"), null)
                        .show()
                } else pickMedia()
            }
            "camera" -> {
                stopPhoneScreen()
                showing2 = "camera"
                startActivity(Intent(this, CameraActivity::class.java)
                    .putExtra(CameraActivity.EXTRA_WIDTH, if (connKind == Link.Kind.WIFI) 1280 else 640))
            }
            "board" -> {
                stopPhoneScreen()
                send(JSONObject().put("c", "board").put("a", "open").put("v", prefs.getString("boardBg", "white")))
                showing2 = "board"
                if (mode != Mode.PEN && mode != Mode.HIGHLIGHT) selectTool(Mode.PEN)   // ready to write
            }
            "doc" -> {
                stopPhoneScreen()
                pickDoc()
            }
            "phone" -> {
                if (Bus.phoneScreenOn) { stopPhoneScreen(); return }   // tap again = stop
                send(JSONObject().put("c", "media").put("a", "hide"))
                togglePhoneScreen()
            }
        }
        buildOptions()
    }

    /** 🔉 / 🔊 — the phone's own loudness (Remco knows it isn't a slide key press). */
    private fun phoneVolume(dir: Int) {
        val am = getSystemService(Context.AUDIO_SERVICE) as android.media.AudioManager
        Bus.ignoreVolumeUntil = System.currentTimeMillis() + 1200
        try {
            am.adjustStreamVolume(android.media.AudioManager.STREAM_MUSIC,
                if (dir > 0) android.media.AudioManager.ADJUST_RAISE else android.media.AudioManager.ADJUST_LOWER,
                android.media.AudioManager.FLAG_SHOW_UI)
        } catch (_: Exception) {}
        val v = try { am.getStreamVolume(android.media.AudioManager.STREAM_MUSIC) } catch (_: Exception) { 0 }
        val max = try { am.getStreamMaxVolume(android.media.AudioManager.STREAM_MUSIC) } catch (_: Exception) { 15 }
        toast("🔊 " + (v * 100 / maxOf(1, max)) + "%")
        Bus.ignoreVolumeUntil = System.currentTimeMillis() + 1200
    }

    /** Turn the phone down to a whisper while its sound goes to the PC; put it back afterwards. */
    private fun quietPhone(on: Boolean) {
        if (!prefs.getBoolean("quietPhone", true)) return
        val am = getSystemService(Context.AUDIO_SERVICE) as android.media.AudioManager
        Bus.ignoreVolumeUntil = System.currentTimeMillis() + 1500
        try {
            val max = am.getStreamMaxVolume(android.media.AudioManager.STREAM_MUSIC)
            if (on) {
                prefs.edit().putInt("volBefore", am.getStreamVolume(android.media.AudioManager.STREAM_MUSIC)).apply()
                am.setStreamVolume(android.media.AudioManager.STREAM_MUSIC, maxOf(1, (max * 0.07f).toInt()), 0)
            } else {
                val before = prefs.getInt("volBefore", -1)
                if (before >= 0) am.setStreamVolume(android.media.AudioManager.STREAM_MUSIC, before.coerceIn(0, max), 0)
            }
        } catch (_: Exception) {}
        Bus.ignoreVolumeUntil = System.currentTimeMillis() + 1500
    }

    // ── 🔈 Sound only on the PC (the PC becomes the phone's Bluetooth speaker) ──
    private var pcSpeaker = false

    @Suppress("MissingPermission")
    private fun togglePcSpeaker() {
        haptic()
        if (!link.isConnected) { toast(L.t("notConnected")); return }
        if (pcSpeaker) {
            send(JSONObject().put("c", "pc_speaker").put("on", false))
            pcSpeaker = false
            if (Bus.phoneScreenOn && prefs.getBoolean("phoneAudio", true)) Bus.setAudio?.invoke(true)
            buildOptions()
            return
        }
        val name = try { if (hasBtPermission()) adapter()?.name ?: "" else "" } catch (_: SecurityException) { "" }
        toast(L.t("speakerConnecting"))
        send(JSONObject().put("c", "pc_speaker").put("on", true).put("name", name))
    }

    private fun onPcSpeaker(m: JSONObject) {
        pcSpeaker = m.optBoolean("on")
        if (pcSpeaker) {
            Bus.setAudio?.invoke(false)   // Android now sends the sound itself — no second copy
            toast(L.t("speakerDone"))
        } else if (m.optString("error").isNotEmpty()) {
            AlertDialog.Builder(this).setTitle(L.t("speaker"))
                .setMessage(L.t("speakerFail") + "\n\n(" + m.optString("error") + ")")
                .setPositiveButton(L.t("openBtSettings")) { _, _ -> openBtSettings() }
                .setNegativeButton(L.t("ok"), null).show()
        }
        if (mode == Mode.MOUSE) buildOptions()
    }

    /**
     * Mute the phone's own speaker while its screen + sound go to the computer.
     * Some phones also silence what is sent to the PC when muted — Remco checks
     * the level for 2 s and turns the sound back on (with a note) if that happens.
     */
    private fun togglePhoneMute() {
        val am = getSystemService(Context.AUDIO_SERVICE) as android.media.AudioManager
        if (Bus.phoneMuted) { unmutePhone(); buildOptions(); return }
        val before = Bus.audioLevel
        Bus.phoneMuted = true
        try { am.adjustStreamVolume(android.media.AudioManager.STREAM_MUSIC, android.media.AudioManager.ADJUST_MUTE, 0) } catch (_: Exception) {}
        buildOptions()
        if (Bus.audioOn && before > 400) {
            main.postDelayed({
                if (Bus.phoneMuted && Bus.audioLevel < 40) {
                    unmutePhone()
                    buildOptions()
                    AlertDialog.Builder(this).setMessage(L.t("muteKillsPc")).setPositiveButton(L.t("ok"), null).show()
                }
            }, 2000)
        }
    }

    private fun unmutePhone() {
        if (!Bus.phoneMuted) return
        val am = getSystemService(Context.AUDIO_SERVICE) as android.media.AudioManager
        try { am.adjustStreamVolume(android.media.AudioManager.STREAM_MUSIC, android.media.AudioManager.ADJUST_UNMUTE, 0) } catch (_: Exception) {}
        main.postDelayed({ Bus.phoneMuted = false }, 300)   // let the un-mute settle before volume keys count again
    }

    private fun stopPhoneScreen() {
        if (Bus.phoneScreenOn) stopService(Intent(this, ProjectionService::class.java))
    }

    private fun pickMedia() {
        if (!link.isConnected) { toast(L.t("notConnected")); return }
        val i = Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
            .setType("*/*")
            .putExtra(Intent.EXTRA_MIME_TYPES, arrayOf("image/*", "video/*"))
            .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
        @Suppress("DEPRECATION")
        startActivityForResult(i, 79)
    }

    private fun startMedia(uris: List<android.net.Uri>) {
        stopPhoneScreen()
        mediaUris = uris
        val go = { files.sendToPc(uris, present = true) }
        if (connKind == Link.Kind.BT) {
            Thread {
                val mb = (files.totalSize(uris) / (1024 * 1024)).toInt()
                main.post {
                    if (mb >= 15) AlertDialog.Builder(this).setMessage(L.f("mediaBt", mb))
                        .setPositiveButton(L.t("yes")) { _, _ -> go() }.setNegativeButton(L.t("cancel"), null).show()
                    else go()
                }
            }.start()
        } else go()
    }

    private fun mediaCmd(a: String, v: Double = 0.0) {
        haptic()
        send(JSONObject().put("c", "media").put("a", a).put("v", v))
    }

    private fun setupMediaBar() {
        val box = findViewById<LinearLayout>(R.id.mediaButtons)
        box.orientation = LinearLayout.VERTICAL
        mediaRow1 = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        mediaRow2 = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER; setPadding(0, dp(4f), 0, 0) }
        box.addView(mediaRow1); box.addView(mediaRow2)
        buildMediaButtons()
        findViewById<SeekBar>(R.id.mediaSeek).setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar?, p: Int, fromUser: Boolean) {}
            override fun onStartTrackingTouch(sb: SeekBar?) { mediaSeeking = true }
            override fun onStopTrackingTouch(sb: SeekBar?) {
                mediaSeeking = false
                if (mediaDur > 0 && sb != null) mediaCmd("seek", sb.progress / 1000.0 * mediaDur)
            }
        })
    }

    private fun buildMediaButtons() {
        mediaRow1.removeAllViews(); mediaRow2.removeAllViews()
        mediaRow1.addView(chip("⏮", false) { mediaCmd("prev") })
        mediaRow1.addView(chip("⏪ 10", false) { mediaCmd("rel", -10.0) })
        mediaRow1.addView(chip("⏯", false) { mediaCmd("toggle") }.apply { tag = "play"; textSize = 16f })
        mediaRow1.addView(chip("10 ⏩", false) { mediaCmd("rel", 10.0) })
        mediaRow1.addView(chip("⏭", false) { mediaCmd("next") })
        mediaRow2.addView(chip("🔉", false) { mediaCmd("vol", -0.1) })
        mediaRow2.addView(chip("🔊", false) { mediaCmd("vol", 0.1) })
        mediaRow2.addView(chip(L.t("mute"), false) { mediaCmd("mute") }.apply { tag = "mute" })
        mediaRow2.addView(chip(L.t("mediaFill"), false) { mediaCmd("fill") })
        mediaRow2.addView(chip(L.t("mediaRotate"), false) { mediaCmd("rotate") })
        mediaRow2.addView(chip(L.t("mediaClose"), false) { mediaCmd("close") })
    }

    private fun onMediaState(m: JSONObject) {
        val bar = findViewById<View>(R.id.mediaBar)
        if (!m.optBoolean("open")) {
            bar.visibility = View.GONE
            mediaCount = m.optInt("count", 0)
            if (showing2 == "media") { showing2 = "ppt"; if (mode == Mode.MOUSE) buildOptions() }
            return
        }
        mediaCount = m.optInt("count", mediaCount)
        if (showing2 != "media") { showing2 = "media"; if (mode == Mode.MOUSE) buildOptions() }
        bar.visibility = View.VISIBLE
        val video = m.optString("kind") == "video"
        val idx = m.optInt("index") + 1
        val count = m.optInt("count")
        findViewById<TextView>(R.id.mediaInfo).text =
            L.f(if (video) "mediaVideo" else "mediaPhoto", idx, count) + "  ·  " + m.optString("name")
        mediaDur = m.optDouble("dur", 0.0)
        val pos = m.optDouble("pos", 0.0)
        val seek = findViewById<SeekBar>(R.id.mediaSeek)
        seek.visibility = if (video) View.VISIBLE else View.GONE
        if (video && !mediaSeeking && mediaDur > 0) seek.progress = (pos / mediaDur * 1000).toInt()
        findViewById<TextView>(R.id.mediaTime).text =
            if (video && mediaDur > 0) "${fmtTime(pos)} / ${fmtTime(mediaDur)}" else ""
        mediaRow1.findViewWithTag<Button>("play")?.text = if (m.optBoolean("playing")) "⏸" else "▶"
        mediaRow2.findViewWithTag<Button>("mute")?.let { b ->
            b.visibility = if (video) View.VISIBLE else View.GONE
            b.text = if (m.optBoolean("muted")) L.t("unmute") else L.t("mute")
            b.isSelected = m.optBoolean("muted")
        }
        pad.fitBitmap = if (m.optBoolean("fill")) 2 else 1
        for (i in 0 until mediaRow1.childCount) {
            val v = mediaRow1.getChildAt(i)
            val isNav = i == 0 || i == mediaRow1.childCount - 1
            v.visibility = if (video || isNav) View.VISIBLE else View.GONE
        }
    }

    private fun fmtTime(t: Double): String {
        val s = t.toInt().coerceAtLeast(0)
        return String.format(Locale.US, "%d:%02d", s / 60, s % 60)
    }

    // ── Files ───────────────────────────────────────────────────────────
    private fun showFilesDialog() {
        AlertDialog.Builder(this)
            .setTitle(L.t("files"))
            .setMessage(L.t("whereFiles"))
            .setPositiveButton(L.t("sendToPc")) { _, _ ->
                if (!link.isConnected) { toast(L.t("notConnected")); return@setPositiveButton }
                val i = Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                    .setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
                @Suppress("DEPRECATION")
                startActivityForResult(i, 78)
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleShareIntent(intent)
    }

    @Suppress("DEPRECATION")
    private fun handleShareIntent(i: Intent?) {
        if (i == null) return
        val uris = ArrayList<android.net.Uri>()
        when (i.action) {
            Intent.ACTION_SEND -> (i.getParcelableExtra<android.net.Uri>(Intent.EXTRA_STREAM))?.let { uris.add(it) }
            Intent.ACTION_SEND_MULTIPLE -> i.getParcelableArrayListExtra<android.net.Uri>(Intent.EXTRA_STREAM)?.let { uris.addAll(it) }
            else -> return
        }
        i.action = null
        if (uris.isEmpty()) return
        val visual = uris.all { u -> (contentResolver.getType(u) ?: "").let { it.startsWith("image/") || it.startsWith("video/") } }
        if (visual && link.isConnected) {
            AlertDialog.Builder(this)
                .setTitle(L.t("mediaTitle"))
                .setMessage(L.t("mediaWhat"))
                .setPositiveButton(L.t("mediaShow")) { _, _ -> startMedia(uris) }
                .setNeutralButton(L.t("mediaSave")) { _, _ -> files.sendToPc(uris) }
                .setNegativeButton(L.t("cancel"), null)
                .show()
            return
        }
        if (link.isConnected) files.sendToPc(uris) else { pendingShare.addAll(uris); toast(L.t("sendLater")) }
    }

    override fun onTransfer(name: String, percent: Int, toPc: Boolean) {
        main.removeCallbacks(hideTransfer)
        findViewById<View>(R.id.transferBar).visibility = View.VISIBLE
        findViewById<TextView>(R.id.transferText).text = L.f(if (toPc) "sending" else "receiving", name, percent)
        findViewById<android.widget.ProgressBar>(R.id.transferProgress).progress = percent
        if (percent >= 100) main.postDelayed(hideTransfer, 3000)
    }

    override fun onReceived(name: String, uri: android.net.Uri?, mime: String) {
        haptic()
        val b = AlertDialog.Builder(this).setMessage(L.f("received", name)).setNegativeButton(L.t("ok"), null)
        if (uri != null) b.setPositiveButton(L.t("open")) { _, _ ->
            try {
                startActivity(Intent.createChooser(Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), name))
            } catch (_: Exception) {}
        }
        b.show()
    }

    override fun onTransferFailed(name: String) {
        main.postDelayed(hideTransfer, 1500)
        toast(L.f("failedFile", name))
    }

    // ── Volume keys → slides, also with the screen locked ───────────────
    /**
     * Volume keys with the screen LOCKED are handled by [RemoteService] (a small
     * foreground service with a notification). Here we only tell it whether a PC
     * is connected, so it re-centres the volume.
     */
    private fun updateVolumeSession() {
        Bus.volumeKeysWanted = prefs.getBoolean("volKeys", true)
        if (link.isConnected) Bus.armVolume?.invoke()
    }

    /** Start / stop the lock-screen helper (only allowed while Remco is on screen). */
    private fun syncRemoteService() {
        Bus.volumeKeysWanted = prefs.getBoolean("volKeys", true)
        if (Bus.volumeKeysWanted) RemoteService.start(this) else RemoteService.stop(this)
    }

    override fun onResume() {
        super.onResume()
        syncRemoteService()
    }

    private fun volumeStep(up: Boolean) {
        val now = System.currentTimeMillis()
        if (now - lastVolStep < 250) return   // one press = one slide, even if the key repeats
        lastVolStep = now
        val upIsNext = prefs.getBoolean("volUpNext", false)
        haptic()
        cmd(if (up == upIsNext) "next" else "prev")
    }

    // ── Real mouse buttons: press = button down, release = button up ────
    @Suppress("ClickableViewAccessibility")
    private fun setupMouseBar() {
        fun hold(b: Button, which: String) = b.setOnTouchListener { v, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { v.isPressed = true; haptic(); flushMoves(); send(JSONObject().put("c", "btn").put("b", which).put("a", "down")) }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> { v.isPressed = false; flushMoves(); send(JSONObject().put("c", "btn").put("b", which).put("a", "up")) }
            }
            true
        }
        hold(findViewById(R.id.btnLeft), "left")
        hold(findViewById(R.id.btnRight), "right")
        findViewById<Button>(R.id.btnKeyboard).setOnClickListener { if (kbOpen) closeKeyboard() else openKeyboard() }
    }

    // ── Phone keyboard → PC (any language, incl. Kurdish) ───────────────
    private fun setupKeyboard() {
        kbInput.setText(KB_SENTINEL)
        kbInput.setSelection(KB_SENTINEL.length)
        kbInput.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun afterTextChanged(ed: Editable?) {
                if (kbResetting || ed == null) return
                val now = ed.toString()
                val old = kbLast
                var p = 0
                while (p < old.length && p < now.length && old[p] == now[p]) p++
                val del = old.length - p
                val ins = now.substring(p)
                if (del > 0) send(JSONObject().put("c", "key").put("k", "backspace").put("n", del))
                if (ins.isNotEmpty()) send(JSONObject().put("c", "type").put("text", ins))
                kbLast = now
                val composing = BaseInputConnection.getComposingSpanStart(ed) != -1
                if (now.isEmpty() || (!composing && (now.endsWith("\n") || now.length > 40))) {
                    main.post {
                        kbResetting = true
                        kbInput.setText(KB_SENTINEL)
                        kbInput.setSelection(KB_SENTINEL.length)
                        kbLast = KB_SENTINEL
                        kbResetting = false
                    }
                }
            }
        })
        buildKbKeys()
    }

    private fun buildKbKeys() {
        val keys = linkedMapOf(
            "Esc" to JSONObject().put("k", "esc"), "Tab" to JSONObject().put("k", "tab"),
            "⌫" to JSONObject().put("k", "backspace"), "⏎" to JSONObject().put("k", "enter"),
            "←" to JSONObject().put("k", "left"), "↑" to JSONObject().put("k", "up"),
            "↓" to JSONObject().put("k", "down"), "→" to JSONObject().put("k", "right"),
            L.t("selAll") to JSONObject().put("k", "a").put("ctrl", true),
            L.t("copy") to JSONObject().put("k", "c").put("ctrl", true),
            L.t("paste") to JSONObject().put("k", "v").put("ctrl", true),
            L.t("undo") to JSONObject().put("k", "z").put("ctrl", true)
        )
        val row = findViewById<LinearLayout>(R.id.kbKeys)
        row.removeAllViews()
        row.addView(chip(L.t("hideKb"), false) { closeKeyboard() })
        for ((label, o) in keys) row.addView(chip(label, false) { haptic(); send(JSONObject(o.toString()).put("c", "key")) })
    }

    private fun openKeyboard() {
        kbOpen = true
        findViewById<View>(R.id.kbBar).visibility = View.VISIBLE
        findViewById<Button>(R.id.btnKeyboard).isSelected = true
        kbInput.requestFocus()
        kbInput.setSelection(kbInput.text.length)
        kbInput.postDelayed({
            (getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager).showSoftInput(kbInput, InputMethodManager.SHOW_IMPLICIT)
        }, 80)
    }

    private fun closeKeyboard() {
        kbOpen = false
        findViewById<View>(R.id.kbBar).visibility = View.GONE
        findViewById<Button>(R.id.btnKeyboard).isSelected = false
        (getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager).hideSoftInputFromWindow(kbInput.windowToken, 0)
        kbInput.clearFocus()
    }

    // ── Hotspot made by the phone itself (no router, no SIM data) ───────
    private fun hotspotPermission(): String =
        if (Build.VERSION.SDK_INT >= 33) Manifest.permission.NEARBY_WIFI_DEVICES else Manifest.permission.ACCESS_FINE_LOCATION

    private fun startPhoneHotspot() {
        if (hotspot != null) { showHotspotInfo(); return }
        val perm = hotspotPermission()
        if (checkSelfPermission(perm) != PackageManager.PERMISSION_GRANTED) {
            toast(L.t("hsNeedPerm"))
            requestPermissions(arrayOf(perm), 43)
            return
        }
        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
        try {
            wm.startLocalOnlyHotspot(object : WifiManager.LocalOnlyHotspotCallback() {
                override fun onStarted(reservation: WifiManager.LocalOnlyHotspotReservation) {
                    hotspot = reservation
                    showHotspotInfo()
                }
                override fun onStopped() { hotspot = null }
                override fun onFailed(reason: Int) {
                    val why = when (reason) { 3 -> "hotspot already on"; 2 -> "tethering not allowed"; 1 -> "no channel"; else -> "code $reason" }
                    AlertDialog.Builder(this@MainActivity).setMessage(L.f("hsFail", why)).setPositiveButton(L.t("ok"), null).show()
                }
            }, main)
        } catch (e: SecurityException) {
            AlertDialog.Builder(this).setMessage(L.f("hsFail", e.message ?: "permission")).setPositiveButton(L.t("ok"), null).show()
        }
    }

    @Suppress("DEPRECATION")
    private fun hotspotInfo(): Triple<String, String, Boolean>? {
        val r = hotspot ?: return null
        return if (Build.VERSION.SDK_INT >= 30) {
            val c = r.softApConfiguration
            Triple((c.ssid ?: "").trim('"'), c.passphrase ?: "", c.securityType == 3 /*WPA3-SAE only*/)
        } else {
            val c = r.wifiConfiguration ?: return null
            Triple((c.SSID ?: "").trim('"'), c.preSharedKey?.trim('"') ?: "", false)
        }
    }

    private fun showHotspotInfo() {
        val (ssid, pass, wpa3) = hotspotInfo() ?: return
        val auto = link.isConnected && connKind == Link.Kind.BT
        if (auto) send(JSONObject().put("c", "join_wifi").put("ssid", ssid).put("pass", pass).put("sec", if (wpa3) "wpa3" else "wpa2"))
        val info = TextView(this).apply {
            text = L.f("hsInfo", ssid, pass)
            textSize = 20f; setTypeface(typeface, Typeface.BOLD); setTextIsSelectable(true)
            setPadding(dp(22f), dp(10f), dp(22f), 0)
        }
        AlertDialog.Builder(this)
            .setTitle("📱 " + L.t("hsOn"))
            .setMessage(if (auto) L.t("hsAuto") else L.t("hsManual"))
            .setView(info)
            .setPositiveButton(L.t("ok"), null)
            .setNeutralButton(L.t("hsOff")) { _, _ -> stopPhoneHotspot() }
            .show()
    }

    private fun stopPhoneHotspot() {
        try { hotspot?.close() } catch (_: Exception) {}
        hotspot = null
    }

    // ── Text label dialog ───────────────────────────────────────────────
    private fun textDialog(id: String?, existing: String, sx: Double, sy: Double) {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20f), dp(8f), dp(20f), 0) }
        val input = EditText(this).apply {
            setText(existing); hint = L.t("labelHint")
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
            maxLines = 4
            setSelection(existing.length)
        }
        box.addView(input)
        val b = AlertDialog.Builder(this)
            .setTitle(if (id == null) L.t("addLabel") else L.t("editLabel"))
            .setView(box)
            .setPositiveButton(L.t("save")) { _, _ ->
                val t = input.text.toString().trim()
                val o = JSONObject().put("c", "text_save").put("text", t)
                if (id != null) o.put("id", id)
                else o.put("sx", sx).put("sy", sy).put("style", textStyle).put("color", textBg)
                    .put("textColor", textColor).put("fontSize", textFont)
                send(o)
            }
            .setNegativeButton(L.t("cancel")) { _, _ -> send(JSONObject().put("c", "ann_preview_end")) }
        if (id != null) b.setNeutralButton(L.t("delete")) { _, _ -> send(JSONObject().put("c", "ann_delete").put("id", id)) }
        val dlg = b.create()
        dlg.window?.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE)
        dlg.show()
        input.requestFocus()
    }

    // ── Rendering ───────────────────────────────────────────────────────
    private fun setStatus(ok: Boolean?, text: String) {
        statusDot.setTextColor(
            when (ok) {
                true -> Color.parseColor("#30D158")
                false -> Color.parseColor("#E5484D")
                null -> Color.parseColor("#FFB020")
            }
        )
        statusText.text = text
        if (ok == false) btnConnect.text = L.t("connect")
    }

    private fun renderState(click: Int = -1, clicks: Int = -1) {
        slideInfo.text = if (total > 0) {
            val shown = if (slide > total) L.t("end") else "$slide"
            "${L.t("slide")} $shown / $total"
        } else "– / –"

        animInfo.text = when {
            !link.isConnected && !everConnected -> ""
            !pptOpen -> L.t("noPpt")
            !showing -> L.t("pptOpen")
            clicks > 0 && click >= 0 -> L.f("anim", minOf(click, clicks), clicks) +
                    if (click >= clicks) " · " + L.t("noAnim") else ""
            else -> ""
        }

        lblCur.text = if (slide in 1..total) "${L.t("current")} · $slide" else L.t("current")
        lblNext.text = if (slide + 1 <= total) "${L.t("nextSlide")} · ${slide + 1}" else L.t("end")
        if (slide + 1 > total) imgNext.setImageBitmap(null)

        notesText.text = if (notes.isBlank()) L.t("noNotes") else notes
        val rtl = looksRtl(notes)
        notesText.gravity = if (rtl) Gravity.END else Gravity.START
        notesText.textDirection = if (rtl) View.TEXT_DIRECTION_RTL else View.TEXT_DIRECTION_LTR

        findViewById<Button>(R.id.btnBlack).isSelected = screenMode == "black"
        findViewById<Button>(R.id.btnWhite).isSelected = screenMode == "white"
    }

    private fun looksRtl(s: String): Boolean {
        for (ch in s) {
            val d = Character.getDirectionality(ch)
            if (d == Character.DIRECTIONALITY_RIGHT_TO_LEFT || d == Character.DIRECTIONALITY_RIGHT_TO_LEFT_ARABIC) return true
            if (d == Character.DIRECTIONALITY_LEFT_TO_RIGHT) return false
        }
        return false
    }

    private fun applyTexts() {
        root.layoutDirection = if (L.ku) View.LAYOUT_DIRECTION_RTL else View.LAYOUT_DIRECTION_LTR
        btnLang.text = if (L.ku) "EN" else "کوردی"
        btnConnect.text = if (connectedName.isNotEmpty()) L.t("disconnect") else L.t("connect")
        toolButtons[Mode.MOUSE]?.text = L.t("mouse")
        toolButtons[Mode.LASER]?.text = L.t("laser")
        toolButtons[Mode.SPOTLIGHT]?.text = L.t("spotlight")
        toolButtons[Mode.LENS]?.text = L.t("lens")
        toolButtons[Mode.ZOOM]?.text = L.t("zoom")
        toolButtons[Mode.PEN]?.text = L.t("pen")
        toolButtons[Mode.HIGHLIGHT]?.text = L.t("highlight")
        toolButtons[Mode.ERASER]?.text = L.t("eraser")
        toolButtons[Mode.NUMBER]?.text = L.t("number")
        toolButtons[Mode.TEXT]?.text = L.t("text")
        findViewById<Button>(R.id.btnClear).text = L.t("clear")
        findViewById<Button>(R.id.btnLeft).text = L.t("leftBtn")
        buildKbKeys()
        findViewById<Button>(R.id.btnRight).text = L.t("rightBtn")
        findViewById<Button>(R.id.btnNotes).text = L.t("notes")
        findViewById<Button>(R.id.btnStart).text = L.t("start")
        findViewById<Button>(R.id.btnStartCur).text = L.t("startCur")
        findViewById<Button>(R.id.btnEnd).text = L.t("endShow")
        findViewById<Button>(R.id.btnBlack).text = L.t("black")
        findViewById<Button>(R.id.btnWhite).text = L.t("white")
        findViewById<Button>(R.id.btnSlides).text = L.t("slides")
        findViewById<Button>(R.id.btnFiles).text = L.t("files")
        findViewById<Button>(R.id.btnQuiz).text = L.t("quiz")
        findViewById<Button>(R.id.btnPicker).text = L.t("picker")
        if (curView.isNotEmpty()) renderViewBar()
        if (::mediaRow1.isInitialized) buildMediaButtons()
        findViewById<Button>(R.id.btnSettings).text = L.t("settings")
        findViewById<Button>(R.id.btnPrev).text = L.t("prev")
        findViewById<Button>(R.id.btnNext).text = L.t("next")
        if (connectedName.isEmpty()) statusText.text = L.t("notConnected")
        else if (link.isConnected) statusText.text = connectedName + (connKind?.let { "  ·  " + kindLabel(it) } ?: "")
        padHint.text = hintFor(mode)
        buildOptions()
        renderState()
    }

    // ── Dialogs ─────────────────────────────────────────────────────────
    private fun showSlidesDialog() {
        val n = maxOf(total, titles.size)
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 20, 40, 0) }
        val input = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            hint = if (n > 0) "1 – $n" else "#"
        }
        box.addView(input)
        val builder = AlertDialog.Builder(this)
            .setTitle(L.t("goTo"))
            .setView(box)
            .setPositiveButton(L.t("ok")) { _, _ ->
                val v = input.text.toString().toIntOrNull()
                if (v != null && v >= 1) goTo(v)
            }
            .setNegativeButton(L.t("cancel"), null)
        if (n > 0) {
            val items = Array(n) { i ->
                val t = titles.getOrNull(i)?.takeIf { it.isNotBlank() } ?: ""
                val marker = if (i + 1 == slide) "▶ " else ""
                "$marker${i + 1}.  $t"
            }
            builder.setItems(items) { _, i -> goTo(i + 1) }
        }
        builder.show()
    }

    private fun goTo(n: Int) { flushMoves(); send(JSONObject().put("c", "goto").put("n", n)) }

    // ── Timer: countdown or stopwatch, optionally on the projector ──────
    private fun showTimerDialog() {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20f), dp(8f), dp(20f), 0) }
        val rg = RadioGroup(this).apply { orientation = RadioGroup.HORIZONTAL }
        val rDown = RadioButton(this).apply { text = L.t("countdown"); id = View.generateViewId() }
        val rUp = RadioButton(this).apply { text = L.t("stopwatch"); id = View.generateViewId() }
        rg.addView(rDown); rg.addView(rUp)
        rg.check(if (timerMode == "up") rUp.id else rDown.id)
        val input = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            hint = L.t("minutes")
            setText(prefs.getInt("timerMin", 10).toString())
        }
        rg.setOnCheckedChangeListener { _, checked -> input.visibility = if (checked == rUp.id) View.GONE else View.VISIBLE }
        input.visibility = if (timerMode == "up") View.GONE else View.VISIBLE
        val cbProj = CheckBox(this).apply { text = L.t("onProjector"); isChecked = timerOnProjector }
        val cbSilent = CheckBox(this).apply { text = L.t("silent"); isChecked = timerSilent }
        box.addView(rg); box.addView(input); box.addView(cbProj); box.addView(cbSilent)

        AlertDialog.Builder(this)
            .setTitle(L.t("timer"))
            .setView(box)
            .setPositiveButton(if (timerRunning) L.t("pauseTimer") else L.t("startTimer")) { _, _ ->
                val newMode = if (rg.checkedRadioButtonId == rUp.id) "up" else "down"
                timerOnProjector = cbProj.isChecked
                timerSilent = cbSilent.isChecked
                prefs.edit().putBoolean("timerProj", timerOnProjector).putBoolean("timerSilent", timerSilent)
                    .putString("timerMode", newMode).apply()
                if (timerRunning) {
                    timerRunning = false
                    main.removeCallbacks(timerTick)
                } else {
                    if (newMode != timerMode) { timerMode = newMode; timerSec = -1 }
                    if (timerMode == "down") {
                        val min = input.text.toString().toIntOrNull()?.coerceIn(1, 600) ?: 10
                        prefs.edit().putInt("timerMin", min).apply()
                        if (timerSec <= 0 || timerTotal != min * 60) { timerTotal = min * 60; timerSec = timerTotal }
                    } else if (timerSec < 0) timerSec = 0
                    timerRunning = true
                    main.removeCallbacks(timerTick)
                    main.postDelayed(timerTick, 1000)
                }
                renderTimer(); sendTimer()
            }
            .setNeutralButton(L.t("resetTimer")) { _, _ ->
                timerOnProjector = cbProj.isChecked
                timerSilent = cbSilent.isChecked
                prefs.edit().putBoolean("timerProj", timerOnProjector).putBoolean("timerSilent", timerSilent).apply()
                resetTimer()
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    private fun resetTimer() {
        timerRunning = false
        main.removeCallbacks(timerTick)
        timerSec = -1; timerTotal = 0
        renderTimer(); sendTimer()
    }

    private fun sendTimer() {
        send(JSONObject().put("c", "timer").put("mode", timerMode).put("sec", timerSec)
            .put("running", timerRunning).put("visible", timerOnProjector && timerSec >= 0))
    }

    /** Same moments as the web app: 59, 3, 2, 1 and 0 seconds left. */
    private fun checkTimerAlerts() {
        val s = timerSec
        if (s !in listOf(59, 3, 2, 1, 0)) return
        val label = if (s == 0) "0" else s.toString()
        showFlash(label, if (s == 0) 1600L else 900L)
        if (!timerSilent) {
            if (s == 0) vibrate(longArrayOf(0, 300, 150, 300, 150, 500)) else vibrate(longArrayOf(0, 90))
        }
        if (timerOnProjector) send(JSONObject().put("c", "timer_alert").put("label", label))
    }

    private val hideFlash = Runnable { flash.visibility = View.GONE }

    private fun showFlash(label: String, ms: Long) {
        flash.text = if (label == "0") "⏰" else label
        flash.setTextColor(Color.parseColor(if (label == "0") "#ef4444" else "#fbbf24"))
        flash.visibility = View.VISIBLE
        flash.scaleX = 0.5f; flash.scaleY = 0.5f; flash.alpha = 0f
        flash.animate().scaleX(1f).scaleY(1f).alpha(1f).setDuration(350).start()
        main.removeCallbacks(hideFlash)
        main.postDelayed(hideFlash, ms)
    }

    private fun renderTimer() {
        val icon = if (timerMode == "up") "⏱" else "⌛"
        timerText.text = if (timerSec < 0) "$icon --:--"
        else String.format(Locale.US, "%s %02d:%02d", icon, timerSec / 60, timerSec % 60)
        timerText.setTextColor(
            when {
                timerMode == "down" && timerSec == 0 -> Color.parseColor("#FF453A")
                timerMode == "down" && timerSec in 1..60 -> Color.parseColor("#FFB020")
                else -> Color.parseColor("#E8EAF0")
            }
        )
    }

    private fun showSettings() {
        val d = resources.displayMetrics.density
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding((20 * d).toInt(), (12 * d).toInt(), (20 * d).toInt(), 0)
        }
        val cbVol = CheckBox(this).apply { text = L.t("volKeys"); isChecked = prefs.getBoolean("volKeys", true) }
        val cbVolSwap = CheckBox(this).apply { text = L.t("volSwap"); isChecked = prefs.getBoolean("volUpNext", false) }
        val cbPrev = CheckBox(this).apply { text = L.t("showPreviews"); isChecked = prefs.getBoolean("previews", true) }
        val cbSlide = CheckBox(this).apply { text = L.t("showSlideOnPad"); isChecked = prefs.getBoolean("showSlide", true) }
        val cbVib = CheckBox(this).apply { text = L.t("vibrate"); isChecked = prefs.getBoolean("vibrate", true) }
        val cbKb = CheckBox(this).apply { text = L.t("autoKb"); isChecked = prefs.getBoolean("autoKb", true) }
        val cbQuiet = CheckBox(this).apply { text = L.t("quietPhone"); isChecked = prefs.getBoolean("quietPhone", true) }
        val hsBtn = chip(L.t("phoneHotspot"), hotspot != null) { startPhoneHotspot() }
        val lbl = TextView(this).apply { text = L.t("sensitivity"); setPadding(0, (12 * d).toInt(), 0, 0) }
        val seek = SeekBar(this).apply {
            max = 25
            progress = ((prefs.getFloat("sens", 1.4f) - 0.5f) * 10f).toInt().coerceIn(0, 25)
        }
        val modeTitle = TextView(this).apply { text = L.t("connMode"); setPadding(0, 0, 0, (4 * d).toInt()); setTypeface(typeface, Typeface.BOLD) }
        val rg = RadioGroup(this)
        val modes = listOf("auto" to L.t("modeAuto"), "wifi" to L.t("modeWifi"), "bt" to L.t("modeBt"))
        val ids = modes.map { (k, t) -> RadioButton(this).apply { text = t; id = View.generateViewId(); tag = k }.also { rg.addView(it) }.id }
        rg.check(ids[modes.indexOfFirst { it.first == connMode() }.coerceAtLeast(0)])
        val ipInfo = TextView(this).apply {
            text = L.f("thisPhone", WifiSide.localAddresses().joinToString(", ").ifBlank { "—" })
            textSize = 12f; setTextColor(Color.parseColor("#8A90A2")); setPadding(0, 0, 0, (10 * d).toInt())
        }
        box.addView(modeTitle); box.addView(rg); box.addView(ipInfo); box.addView(hsBtn)
        box.addView(cbVol); box.addView(cbVolSwap); box.addView(cbPrev); box.addView(cbSlide); box.addView(cbVib); box.addView(cbKb); box.addView(cbQuiet); box.addView(lbl); box.addView(seek)
        val scroll = ScrollView(this).apply { addView(box) }
        AlertDialog.Builder(this)
            .setTitle(L.t("settingsTitle"))
            .setView(scroll)
            .setPositiveButton(L.t("ok")) { _, _ ->
                val sens = 0.5f + seek.progress / 10f
                val newMode = rg.findViewById<RadioButton>(rg.checkedRadioButtonId)?.tag as? String ?: "auto"
                val oldMode = connMode()
                prefs.edit().putString("connMode", newMode).apply()
                if (newMode != oldMode) {
                    if (newMode == "bt" && connKind == Link.Kind.WIFI) { link.disconnect(); autoConnectBt() }
                    if (newMode == "wifi" && connKind == Link.Kind.BT) link.disconnect()
                }
                prefs.edit()
                    .putBoolean("volKeys", cbVol.isChecked)
                    .putBoolean("volUpNext", cbVolSwap.isChecked)
                    .putBoolean("previews", cbPrev.isChecked)
                    .putBoolean("showSlide", cbSlide.isChecked)
                    .putBoolean("vibrate", cbVib.isChecked)
                    .putBoolean("autoKb", cbKb.isChecked)
                    .putBoolean("quietPhone", cbQuiet.isChecked)
                    .putFloat("sens", sens)
                    .apply()
                pad.sensitivity = sens
                pad.showSlide = cbSlide.isChecked
                syncRemoteService()
                updateVolumeSession()
                previewRow.visibility = if (cbPrev.isChecked) View.VISIBLE else View.GONE
            }
            .setNegativeButton(L.t("cancel"), null)
            .show()
    }

    // ── Hardware keys: volume = next / previous ─────────────────────────
    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (prefs.getBoolean("volKeys", true) && link.isConnected) {
            when (keyCode) {
                KeyEvent.KEYCODE_VOLUME_DOWN -> { if (event.repeatCount == 0) volumeStep(false); return true }
                KeyEvent.KEYCODE_VOLUME_UP -> { if (event.repeatCount == 0) volumeStep(true); return true }
            }
        }
        return super.onKeyDown(keyCode, event)
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_LONG).show()

    private fun vibrator(): Vibrator? = getSystemService(Vibrator::class.java)

    private fun haptic() {
        if (!prefs.getBoolean("vibrate", true)) return
        try { vibrator()?.vibrate(VibrationEffect.createOneShot(18, VibrationEffect.DEFAULT_AMPLITUDE)) } catch (_: Exception) {}
    }

    private fun vibrate(pattern: LongArray) {
        try { vibrator()?.vibrate(VibrationEffect.createWaveform(pattern, -1)) } catch (_: Exception) {}
    }
}
