import { useDebounceFn, useEventListener } from "@vueuse/core";
import { computed, onScopeDispose, reactive, ref, watch } from "vue";
import { toast } from "vue-sonner";

import { api, type Item, type ItemPatch, type TagCount } from "@/lib/api";
import type { Capture } from "@/lib/capture";

const PAGE = 50;

function fail(action: string, error: unknown) {
  const value = (error as { value?: unknown } | null)?.value;
  toast.error(`Couldn't ${action}`, { description: typeof value === "string" ? value : undefined });
}

/** The feed, its filters, and every action on items. Stays live through the server's change feed. */
export function useItems() {
  const filters = reactive({ q: "", tags: [] as string[], archived: false });
  const items = ref<Item[]>([]);
  const tags = ref<TagCount[]>([]);
  const hasMore = ref(false);
  const loading = ref(false);
  let generation = 0;

  async function fetchPage(offset: number, limit: number) {
    const { data, error } = await api.items.get({
      query: {
        q: filters.q.trim() || undefined,
        tags: filters.tags.length ? filters.tags.join(",") : undefined,
        archived: filters.archived,
        limit,
        offset,
      },
    });
    if (error) throw error;
    return data;
  }

  /** Reloads what's on screen (at least one page), keeping the scroll position useful. */
  async function reload() {
    const gen = ++generation;
    loading.value = true;
    try {
      const [page, tagRes] = await Promise.all([fetchPage(0, Math.max(PAGE, items.value.length)), api.tags.get()]);
      if (gen !== generation) return; // a newer reload won
      items.value = page.items;
      hasMore.value = page.hasMore;
      if (tagRes.data) tags.value = tagRes.data;
    } catch (error) {
      if (gen === generation) fail("load your items", error);
    } finally {
      if (gen === generation) loading.value = false;
    }
  }

  async function loadMore() {
    if (loading.value || !hasMore.value) return;
    loading.value = true;
    try {
      const page = await fetchPage(items.value.length, PAGE);
      items.value.push(...page.items.filter((i) => !items.value.some((e) => e.id === i.id)));
      hasMore.value = page.hasMore;
    } catch (error) {
      fail("load more items", error);
    } finally {
      loading.value = false;
    }
  }

  const reloadSoon = useDebounceFn(reload, 150);
  watch(
    () => [filters.q, filters.tags.join(","), filters.archived],
    () => {
      items.value = [];
      reloadSoon();
    },
  );

  // --- actions -------------------------------------------------------------

  function prepend(item: Item) {
    if (!filters.archived) items.value = [item, ...items.value.filter((i) => i.id !== item.id)];
  }

  /**
   * Saves a note, or one item per file (each with the same text and tags).
   * Returns the files that couldn't be saved, so the composer can keep them.
   */
  async function create(capture: Capture | null, files: File[] = []): Promise<{ saved: boolean; failed: File[] }> {
    const fields = {
      text: capture?.text ?? "",
      url: capture?.url ?? null,
      title: capture?.title ?? null,
      tags: capture?.tags ?? [],
    };

    if (files.length === 0) {
      const { data, error } = await api.items.post(fields);
      if (error) {
        fail("save that", error);
        return { saved: false, failed: [] };
      }
      prepend(data);
      reloadSoon();
      return { saved: true, failed: [] };
    }

    const failed: File[] = [];
    const label = files.length === 1 ? files[0]!.name : `${files.length} files`;
    const progress = toast.loading(`Uploading ${label}…`);
    // One at a time on purpose: items keep the order they were picked in, and big
    // videos don't compete for bandwidth.
    for (const file of files) {
      // oxlint-disable-next-line no-await-in-loop
      const upload = await api.files.post({ file });
      if (upload.error) {
        failed.push(file);
        fail(`upload ${file.name}`, upload.error);
        continue;
      }
      // oxlint-disable-next-line no-await-in-loop
      const { data, error } = await api.items.post({ ...fields, fileId: upload.data.id });
      if (error) {
        failed.push(file);
        fail(`save ${file.name}`, error);
        continue;
      }
      prepend(data);
    }
    toast.dismiss(progress);
    reloadSoon();
    return { saved: failed.length < files.length, failed };
  }

  async function update(item: Item, patch: ItemPatch) {
    const { data, error } = await api.items({ id: item.id }).patch(patch);
    if (error) return fail("update that item", error);
    const leaves = patch.archived !== undefined && patch.archived !== filters.archived;
    items.value = leaves
      ? items.value.filter((i) => i.id !== item.id)
      : items.value.map((i) => (i.id === item.id ? data : i));
    reloadSoon();
  }

  async function remove(item: Item) {
    const { error } = await api.items({ id: item.id }).delete();
    if (error) return fail("delete that item", error);
    items.value = items.value.filter((i) => i.id !== item.id);
    reloadSoon();
    toast("Deleted", {
      action: {
        label: "Undo",
        onClick: async () => {
          // Re-posting the same id revives the item (server-side writes always win).
          const { error: e } = await api.items.post({
            id: item.id,
            text: item.text,
            url: item.url,
            title: item.title,
            fileId: item.file?.id ?? null,
            pinned: item.pinned,
            tags: item.tags,
          });
          if (e) return fail("restore that item", e);
          if (item.archived) await api.items({ id: item.id }).patch({ archived: true });
          reload();
        },
      },
    });
  }

  // --- live updates ----------------------------------------------------------

  let lastRev: number | null = null;
  const events = new EventSource("/api/sync/events", { withCredentials: true });
  events.addEventListener("rev", (e) => {
    const { rev } = JSON.parse((e as MessageEvent<string>).data) as { rev: number };
    if (lastRev !== null && rev !== lastRev) reloadSoon();
    lastRev = rev;
  });
  // Catch up after the laptop wakes or the tab comes back.
  useEventListener(document, "visibilitychange", () => document.visibilityState === "visible" && reloadSoon());
  onScopeDispose(() => events.close());

  reload();

  return {
    filters,
    items,
    tags,
    hasMore,
    loading,
    empty: computed(() => !loading.value && items.value.length === 0),
    reload,
    loadMore,
    create,
    update,
    remove,
  };
}
