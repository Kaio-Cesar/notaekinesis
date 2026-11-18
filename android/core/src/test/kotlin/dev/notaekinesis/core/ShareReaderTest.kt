package dev.notaekinesis.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ShareReaderTest {
    @Test
    fun a_shared_url_takes_the_subject_as_its_title() {
        val c = ShareReader.read("https://bun.sh/docs", "Bun Docs")!!
        assertEquals("https://bun.sh/docs", c.url)
        assertEquals("Bun Docs", c.title)
        assertEquals("", c.text)
        assertNull(ShareReader.read("https://example.com/", "https://example.com")!!.title)
    }

    @Test
    fun a_title_and_url_together_become_a_titled_link() {
        val c = ShareReader.read("Why SQLite is great - https://x.dev/sqlite")!!
        assertEquals("https://x.dev/sqlite", c.url)
        assertEquals("Why SQLite is great", c.title)
        assertEquals("https://x.dev/a", ShareReader.read("https://x.dev/a\nA title on the next line")!!.url)
    }

    @Test
    fun other_text_is_saved_as_it_is() {
        val note = ShareReader.read("  #1 rule: no tags from shared text\nsecond line ")!!
        assertEquals("#1 rule: no tags from shared text\nsecond line", note.text)
        assertNull(note.url)
        assertEquals(emptyList(), note.tags)
        // A URL in the middle of a longer text, or several URLs, stay in the text.
        assertNull(ShareReader.read("see https://a.dev for more about it")!!.url)
        assertNull(ShareReader.read("https://a.dev and https://b.dev")!!.url)
    }

    @Test
    fun nothing_useful_gives_null() {
        assertNull(ShareReader.read("   "))
        assertNull(ShareReader.read(null))
        assertEquals("Just a subject", ShareReader.read(null, "Just a subject")!!.text)
    }
}
