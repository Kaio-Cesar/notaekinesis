package dev.notaekinesis.core

/**
 * Turns text shared from another app into an item. Like a drop on the Windows basket, shared text
 * is saved as it is (a '#' in someone else's text is not meant as a tag), but a shared link is
 * recognised in the shapes apps send it: the URL alone (with the page title as the subject), or
 * a short title and the URL together ("Some article https://…").
 */
object ShareReader {
    private val singleUrl = Regex("""^https?://[^\s<>"'`]+$""", RegexOption.IGNORE_CASE)
    private val anyUrl = Regex("""https?://[^\s<>"'`]+""", RegexOption.IGNORE_CASE)
    private val space = Regex("""\s+""")
    private val separators = " \t-–—|:·•"

    fun read(text: String?, subject: String? = null): Capture? {
        val plain = text?.trim().orEmpty()
        val title = clean(subject)
        if (plain.isEmpty()) return title?.let { Capture(it, null, emptyList()) }
        if (singleUrl.matches(plain)) return Capture("", plain, emptyList(), title?.takeIf { !it.sameUrl(plain) && "://" !in it })

        val urls = anyUrl.findAll(plain).toList()
        if (urls.size == 1) {
            val url = urls[0]
            val atEdge = url.range.first == 0 || url.range.last == plain.length - 1
            val rest = clean(plain.removeRange(url.range).trim(*separators.toCharArray()))
            if (atEdge && (rest == null || rest.length <= 300)) {
                return Capture("", url.value, emptyList(), rest ?: title?.takeIf { "://" !in it })
            }
        }
        return Capture(plain, null, emptyList())
    }

    private fun clean(s: String?): String? = s?.let { space.replace(it, " ").trim() }?.ifEmpty { null }

    private fun String.sameUrl(other: String) = trim().trimEnd('/').equals(other.trim().trimEnd('/'), ignoreCase = true)
}
