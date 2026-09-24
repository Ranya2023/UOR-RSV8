package edu.uor.remco

import java.util.Random

/** Pure logic of the 🎲 random picker (kept separate so it can be tested on its own). */
object PickerNames {
    private val range = Regex("^(\\d{1,3})\\s*[-–]\\s*(\\d{1,3})$")

    /** "1-30" (or "1–30") → 1 … 30; otherwise one name per line (blank lines ignored). */
    fun parse(text: String): List<String> {
        val t = text.trim()
        range.matchEntire(t)?.let { m ->
            val a = m.groupValues[1].toInt(); val b = m.groupValues[2].toInt()
            if (b >= a && b - a < 500) return (a..b).map { it.toString() }
        }
        return t.lines().map { it.trim() }.filter { it.isNotEmpty() }
    }

    /**
     * Picks a winner. With noRepeat, [pool] holds who is still left this round;
     * when it runs out, a new round starts with everybody.
     * Returns (winner, how many are left this round — or -1 without noRepeat).
     */
    fun pick(all: List<String>, pool: MutableList<String>, noRepeat: Boolean, rnd: Random): Pair<String, Int> {
        if (!noRepeat) return all[rnd.nextInt(all.size)] to -1
        pool.retainAll(all.toSet())
        if (pool.isEmpty()) pool.addAll(all)
        val w = pool.removeAt(rnd.nextInt(pool.size))
        return w to pool.size
    }
}
