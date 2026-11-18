package dev.notaekinesis.app.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.notaekinesis.app.AppSession
import dev.notaekinesis.core.CaptureParser
import dev.notaekinesis.core.Item
import dev.notaekinesis.core.ItemQuery
import dev.notaekinesis.core.TagCount
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

data class Filter(val search: String = "", val tags: List<String> = emptyList(), val archived: Boolean = false)

data class Feed(val items: List<Item> = emptyList(), val tags: List<TagCount> = emptyList(), val loaded: Boolean = false)

/** The feed: local queries only, re-run whenever the store changes (a local edit or a sync). */
class FeedViewModel(private val session: AppSession) : ViewModel() {
    private val store get() = session.store

    val filter = MutableStateFlow(Filter())

    @OptIn(FlowPreview::class)
    val feed: StateFlow<Feed> = combine(store.changes, filter.debounce { if (it.search.isEmpty()) 0 else 120 }) { _, f -> f }
        .map { f ->
            Feed(
                items = store.query(ItemQuery(f.search.ifBlank { null }, f.tags, f.archived)),
                tags = store.tags(f.archived),
                loaded = true,
            )
        }
        .flowOn(Dispatchers.IO)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), Feed())

    fun setSearch(text: String) = filter.value.let { filter.value = it.copy(search = text) }

    fun toggleTag(tag: String) = filter.value.let {
        filter.value = it.copy(tags = if (tag in it.tags) it.tags - tag else it.tags + tag)
    }

    fun setArchived(on: Boolean) {
        filter.value = filter.value.copy(archived = on, tags = emptyList())
    }

    /** Saves the composer's text. Returns false if there was nothing to save. */
    fun add(text: String): Boolean {
        val capture = CaptureParser.parse(text) ?: return false
        // New items inherit the tags being filtered on, so they don't vanish from view.
        val tags = capture.tags + filter.value.tags
        write { store.save(Item.new(capture.text, capture.url, tags)) }
        return true
    }

    /** Applies an edit made in the editor. A changed link drops the old link's preview. */
    fun edit(item: Item, text: String) {
        val capture = CaptureParser.parse(text)
        val url = capture?.url
        val sameLink = url == item.url
        write {
            store.save(
                item.copy(
                    text = capture?.text ?: "",
                    url = url,
                    tags = capture?.tags ?: emptyList(),
                    title = if (sameLink) item.title else null,
                    description = if (sameLink) item.description else null,
                    siteName = if (sameLink) item.siteName else null,
                    faviconUrl = if (sameLink) item.faviconUrl else null,
                ),
            )
        }
    }

    fun save(item: Item) = write { store.save(item) }

    fun delete(item: Item) = write { store.delete(item) }

    /** Puts back the version from before a delete or archive (as a new edit, so it syncs). */
    fun restore(previous: Item) = write { store.save(previous) }

    private fun write(block: () -> Unit) {
        viewModelScope.launch(Dispatchers.IO) { block() }
    }
}
