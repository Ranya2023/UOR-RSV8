package edu.uor.remco

/** Pure helpers for the quiz dialogs (tested on their own). */
object QuizText {
    /** Trim answers and drop empty ones at the end — but always keep at least two (A and B). */
    fun trimAnswers(raw: List<String>): List<String> {
        val t = raw.map { it.trim() }.toMutableList()
        while (t.size > 2 && t.last().isEmpty()) t.removeAt(t.size - 1)
        while (t.size < 2) t.add("")
        return t
    }

    /** Quick question: exactly [count] answers (A…), each with the words the teacher typed (may be empty). */
    fun quickAnswers(raw: List<String>, count: Int): List<String> {
        val n = count.coerceIn(2, 6)
        return (0 until n).map { raw.getOrNull(it)?.trim() ?: "" }
    }
}
