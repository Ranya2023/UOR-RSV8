package edu.uor.remco

import java.nio.ByteBuffer

/** Camera frame (YUV_420_888) → NV21 bytes, which Android can turn into JPEG. */
object Yuv {
    fun toNv21(
        y: ByteBuffer, yRow: Int, yPix: Int,
        u: ByteBuffer, v: ByteBuffer, uvRow: Int, uvPix: Int,
        w: Int, h: Int
    ): ByteArray {
        val ySize = w * h
        val nv21 = ByteArray(ySize + ySize / 2)
        var pos = 0
        if (yRow == w && yPix == 1) {
            y.position(0); y.get(nv21, 0, ySize); pos = ySize
        } else {
            for (row in 0 until h) {
                var idx = row * yRow
                for (col in 0 until w) { nv21[pos++] = y.get(idx); idx += yPix }
            }
        }
        for (row in 0 until h / 2) {
            for (col in 0 until w / 2) {
                val i = row * uvRow + col * uvPix
                nv21[pos++] = v.get(i)     // NV21 is V then U
                nv21[pos++] = u.get(i)
            }
        }
        return nv21
    }
}
