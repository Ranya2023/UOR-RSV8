package edu.uor.remco

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiConfiguration
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.os.Build
import android.os.Handler
import android.os.Looper

/**
 * Joins the laptop's hotspot from the scanned QR code (no typing).
 *  • Android 10+: Android shows a small "Connect to <name>?" question; after that
 *    Remco uses that Wi-Fi while the phone keeps its mobile data for the internet.
 *  • Android 8–9: the network is added and joined directly.
 */
class WifiJoiner(context: Context) {

    interface Callback {
        fun onJoined()
        fun onJoinFailed()
    }

    private val ctx = context.applicationContext
    private val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    private val main = Handler(Looper.getMainLooper())
    private var callback: ConnectivityManager.NetworkCallback? = null

    fun join(ssid: String, pass: String, cb: Callback) {
        release()
        if (Build.VERSION.SDK_INT >= 29) {
            val spec = WifiNetworkSpecifier.Builder().setSsid(ssid).apply {
                if (pass.isNotEmpty()) setWpa2Passphrase(pass)
            }.build()
            val req = NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .setNetworkSpecifier(spec)
                .build()
            val c = object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    // make Remco's own connections use the hotspot (the internet stays on mobile data)
                    cm.bindProcessToNetwork(network)
                    main.post { cb.onJoined() }
                }
                override fun onUnavailable() { main.post { cb.onJoinFailed() } }
                override fun onLost(network: Network) { cm.bindProcessToNetwork(null) }
            }
            callback = c
            try { cm.requestNetwork(req, c) } catch (_: Exception) { cb.onJoinFailed() }
        } else {
            @Suppress("DEPRECATION")
            try {
                val wm = ctx.getSystemService(Context.WIFI_SERVICE) as WifiManager
                if (!wm.isWifiEnabled) wm.isWifiEnabled = true
                val conf = WifiConfiguration().apply {
                    SSID = "\"" + ssid + "\""
                    if (pass.isNotEmpty()) preSharedKey = "\"" + pass + "\""
                    else allowedKeyManagement.set(WifiConfiguration.KeyMgmt.NONE)
                }
                val id = wm.addNetwork(conf)
                if (id == -1) { cb.onJoinFailed(); return }
                wm.disconnect()
                wm.enableNetwork(id, true)
                wm.reconnect()
                main.postDelayed({ cb.onJoined() }, 4000)
            } catch (_: Exception) { cb.onJoinFailed() }
        }
    }

    fun release() {
        callback?.let { try { cm.unregisterNetworkCallback(it) } catch (_: Exception) {} }
        callback = null
        try { cm.bindProcessToNetwork(null) } catch (_: Exception) {}
    }
}
