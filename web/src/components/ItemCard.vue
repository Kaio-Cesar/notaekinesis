<script setup lang="ts">
import {
  ArchiveIcon,
  ArchiveRestoreIcon,
  CopyIcon,
  EllipsisIcon,
  DownloadIcon,
  FileTextIcon,
  GlobeIcon,
  PencilIcon,
  PinIcon,
  PinOffIcon,
  Trash2Icon,
} from "@lucide/vue";
import { useClipboard, useResizeObserver, useTimeAgo } from "@vueuse/core";
import { computed, nextTick, ref, watch } from "vue";
import { toast } from "vue-sonner";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import type { Item, ItemPatch } from "@/lib/api";
import { parseCapture, toCaptureText } from "@/lib/capture";
import { fileUrl, formatSize, mediaKind } from "@/lib/files";
import { renderMarkdown } from "@/lib/markdown";
import { cn } from "@/lib/utils";

const props = defineProps<{
  item: Item;
  activeTags: string[];
  /** Grid cards clamp long notes so one item can't take over a column; double-click to see it all. */
  compact?: boolean;
}>();
const emit = defineEmits<{ update: [patch: ItemPatch]; remove: []; toggleTag: [tag: string] }>();

const ago = useTimeAgo(() => props.item.createdAt);
const { copy } = useClipboard({ legacy: true });

const host = computed(() => {
  if (!props.item.url) return null;
  try {
    return new URL(props.item.url).hostname.replace(/^www\./, "");
  } catch {
    return props.item.url;
  }
});

// --- Markdown body; in the grid, long notes are cut off with a fade -------------
const html = computed(() => (props.item.text ? renderMarkdown(props.item.text) : ""));
const body = ref<HTMLElement>();
const overflowing = ref(false);
function measure() {
  overflowing.value = !!body.value && body.value.scrollHeight > body.value.clientHeight + 1;
}
useResizeObserver(body, measure);
// A capped card doesn't change size when its content grows, so re-measure on edits too.
watch(html, () => nextTick(measure));

// --- inline edit: the same syntax as quick-add (link, Markdown text, #tags) ---
const editing = ref(false);
const draft = ref("");
const editor = ref<HTMLTextAreaElement>();

async function startEdit() {
  draft.value = toCaptureText(props.item);
  editing.value = true;
  await nextTick();
  editor.value?.focus();
}

function saveEdit() {
  const parsed = parseCapture(draft.value);
  editing.value = false;
  if (!parsed) return;
  emit("update", { text: parsed.text, url: parsed.url, tags: parsed.tags });
}

function onEditKeydown(e: KeyboardEvent) {
  if (e.key === "Escape") editing.value = false;
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) saveEdit();
}

// --- attached file ---------------------------------------------------------------
const media = computed(() => (props.item.file ? mediaKind(props.item.file.mime) : null));
const fileHref = computed(() => (props.item.file ? fileUrl(props.item.file.id) : ""));

async function copyItem() {
  const { file, url, text } = props.item;
  await copy(file ? new URL(fileUrl(file.id), window.location.origin).href : (url ?? text));
  toast.success("Copied");
}
</script>

<template>
  <article
    :class="
      cn(
        'group relative rounded-xl border bg-card px-4 py-3 text-card-foreground transition-colors hover:border-ring/40',
        item.pinned && 'border-primary/30 bg-accent/40',
      )
    "
  >
    <div v-if="editing" class="space-y-2">
      <textarea
        ref="editor"
        v-model="draft"
        rows="4"
        class="block w-full resize-y rounded-md border bg-background px-3 py-2 text-sm outline-none focus:ring-3 focus:ring-ring/30"
        @keydown="onEditKeydown"
      />
      <div class="flex justify-end gap-2">
        <Button size="sm" variant="ghost" @click="editing = false">Cancel</Button>
        <Button size="sm" @click="saveEdit">Save <span class="text-xs opacity-60">Ctrl+Enter</span></Button>
      </div>
    </div>

    <template v-else>
      <template v-if="item.file">
        <a v-if="media === 'image'" :href="fileHref" target="_blank" class="-mx-1 block">
          <img
            :src="fileHref"
            :alt="item.file.name"
            loading="lazy"
            :class="cn('w-full rounded-lg bg-muted object-contain', compact ? 'max-h-96' : 'max-h-[32rem]')"
          />
        </a>
        <video
          v-else-if="media === 'video'"
          :src="fileHref"
          controls
          playsinline
          preload="metadata"
          :class="cn('-mx-1 block w-[calc(100%+0.5rem)] rounded-lg bg-black', compact ? 'max-h-96' : 'max-h-[32rem]')"
        />
        <a
          v-else
          :href="fileHref"
          target="_blank"
          class="flex min-w-0 items-center gap-3 rounded-lg border bg-muted/40 p-2 pr-16 hover:bg-muted"
        >
          <span
            class="flex size-10 shrink-0 items-center justify-center rounded-md bg-background text-muted-foreground"
          >
            <FileTextIcon class="size-5" />
          </span>
          <span class="min-w-0">
            <span class="block truncate text-sm font-medium">{{ item.file.name }}</span>
            <span class="block text-xs text-muted-foreground">
              {{ media === "pdf" ? "PDF" : item.file.mime }} · {{ formatSize(item.file.size) }}
            </span>
          </span>
        </a>
      </template>
      <a
        v-if="item.url"
        :href="item.url"
        target="_blank"
        rel="noopener noreferrer"
        :class="cn('flex min-w-0 items-center gap-2 pr-16 hover:underline', item.file && 'mt-2')"
      >
        <img v-if="item.faviconUrl" :src="item.faviconUrl" alt="" class="size-4 shrink-0 rounded-sm" />
        <GlobeIcon v-else class="size-4 shrink-0 text-muted-foreground" />
        <span class="truncate font-medium">{{ item.title || host }}</span>
        <span v-if="item.title" class="hidden truncate text-xs text-muted-foreground sm:inline">{{ host }}</span>
      </a>
      <p v-if="item.description" class="mt-1 line-clamp-2 text-sm text-muted-foreground">{{ item.description }}</p>
      <!-- v-html is safe here: markdown-it runs with html: false (see lib/markdown.ts) -->
      <div
        v-if="item.text"
        ref="body"
        :class="
          cn(
            'prose prose-sm max-w-none break-words',
            'prose-headings:mt-3 prose-headings:mb-1 prose-p:my-1 prose-ol:my-1 prose-ul:my-1 prose-li:my-0',
            'prose-pre:my-2 prose-pre:text-xs prose-code:before:content-none prose-code:after:content-none',
            item.url || item.file ? 'mt-2' : 'pr-16',
            compact && 'max-h-72 overflow-hidden',
            compact && overflowing && 'mask-b-from-75%',
          )
        "
        @dblclick="startEdit"
        v-html="html"
      />

      <footer class="mt-2 flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
        <PinIcon v-if="item.pinned" class="size-3 text-foreground" />
        <time :datetime="new Date(item.createdAt).toISOString()" :title="new Date(item.createdAt).toLocaleString()">
          {{ ago }}
        </time>
        <Badge
          v-for="tag in item.tags"
          :key="tag"
          as="button"
          :variant="activeTags.includes(tag) ? 'default' : 'secondary'"
          class="cursor-pointer"
          @click="emit('toggleTag', tag)"
        >
          #{{ tag }}
        </Badge>
      </footer>

      <div
        class="absolute top-2 right-2 flex gap-0.5 rounded-md bg-card/80 backdrop-blur-sm opacity-100 transition-opacity sm:opacity-0 sm:group-focus-within:opacity-100 sm:group-hover:opacity-100"
      >
        <Button
          size="icon-sm"
          variant="ghost"
          :aria-label="item.pinned ? 'Unpin' : 'Pin'"
          @click="emit('update', { pinned: !item.pinned })"
        >
          <PinOffIcon v-if="item.pinned" />
          <PinIcon v-else />
        </Button>
        <DropdownMenu>
          <DropdownMenuTrigger as-child>
            <Button size="icon-sm" variant="ghost" aria-label="More actions"><EllipsisIcon /></Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem @select="startEdit"><PencilIcon /> Edit</DropdownMenuItem>
            <DropdownMenuItem @select="copyItem">
              <CopyIcon /> Copy {{ item.file ? "file link" : item.url ? "link" : "text" }}
            </DropdownMenuItem>
            <DropdownMenuItem v-if="item.file" as-child>
              <a :href="fileHref" :download="item.file.name"><DownloadIcon /> Download</a>
            </DropdownMenuItem>
            <DropdownMenuItem @select="emit('update', { archived: !item.archived })">
              <ArchiveRestoreIcon v-if="item.archived" />
              <ArchiveIcon v-else />
              {{ item.archived ? "Unarchive" : "Archive" }}
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem variant="destructive" @select="emit('remove')"><Trash2Icon /> Delete</DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </template>
  </article>
</template>
