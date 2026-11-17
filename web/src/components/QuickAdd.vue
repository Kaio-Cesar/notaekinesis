<script setup lang="ts">
import { FileTextIcon, FilmIcon, HashIcon, LinkIcon, PaperclipIcon, SendHorizontalIcon, XIcon } from "@lucide/vue";
import { useTextareaAutosize } from "@vueuse/core";
import { computed, nextTick, onBeforeUnmount, ref, shallowRef } from "vue";
import { toast } from "vue-sonner";

import { Button } from "@/components/ui/button";
import { parseCapture, type Capture } from "@/lib/capture";
import { ACCEPT, formatSize, isSupportedFile, mediaKind } from "@/lib/files";

const props = defineProps<{
  /** Saves the note (or one item per attached file) and reports which files failed. */
  onSubmit: (capture: Capture | null, files: File[]) => Promise<{ saved: boolean; failed: File[] }>;
}>();

const { textarea, input } = useTextareaAutosize();
const preview = computed(() => parseCapture(input.value ?? ""));

// --- attachments --------------------------------------------------------------
interface Attachment {
  key: number;
  file: File;
  /** Object URL for image thumbnails. */
  thumb: string | null;
}
let nextKey = 0;
const attachments = shallowRef<Attachment[]>([]);
const picker = ref<HTMLInputElement>();

function attach(files: Iterable<File>) {
  const added: Attachment[] = [];
  for (const file of files) {
    if (!isSupportedFile(file)) {
      toast.error(`Can't attach ${file.name}`, { description: "Images, videos and PDFs only." });
      continue;
    }
    const thumb = mediaKind(file.type) === "image" ? URL.createObjectURL(file) : null;
    added.push({ key: nextKey++, file, thumb });
  }
  if (added.length) attachments.value = [...attachments.value, ...added];
  textarea.value?.focus();
}

function detach(key: number) {
  const gone = attachments.value.find((a) => a.key === key);
  if (gone?.thumb) URL.revokeObjectURL(gone.thumb);
  attachments.value = attachments.value.filter((a) => a.key !== key);
}

function clearAttachments() {
  for (const a of attachments.value) if (a.thumb) URL.revokeObjectURL(a.thumb);
  attachments.value = [];
}
onBeforeUnmount(clearAttachments);

function onPicked(e: Event) {
  const el = e.target as HTMLInputElement;
  if (el.files) attach(el.files);
  el.value = "";
}

function onPaste(e: ClipboardEvent) {
  const files = e.clipboardData?.files;
  if (files?.length) {
    e.preventDefault();
    attach(files);
  }
}

/** Puts text (e.g. a dropped link) into the box so a note can be added before saving. */
async function insertText(text: string) {
  const current = input.value ?? "";
  input.value = current.trim() ? `${current.trimEnd()}\n${text}` : `${text}\n`;
  await nextTick();
  const el = textarea.value;
  if (el) {
    el.focus();
    el.setSelectionRange(el.value.length, el.value.length);
  }
}

const canSave = computed(() => !!preview.value || attachments.value.length > 0);

/**
 * Clears the box right away so you can keep typing (like a chat), and puts
 * the text back only if saving failed and nothing new was typed meanwhile.
 */
async function submit() {
  if (!canSave.value) return;
  const capture = preview.value;
  const raw = input.value;
  const pending = attachments.value;
  input.value = "";
  attachments.value = [];
  try {
    const { saved, failed } = await props.onSubmit(
      capture,
      pending.map((a) => a.file),
    );
    // Keep what didn't make it, so it can be retried; drop the rest.
    const kept = pending.filter((a) => failed.includes(a.file));
    for (const a of pending) if (a.thumb && !kept.includes(a)) URL.revokeObjectURL(a.thumb);
    if (kept.length) attachments.value = [...kept, ...attachments.value];
    if ((!saved || kept.length) && !input.value) input.value = raw;
  } finally {
    textarea.value?.focus();
  }
}

/** True while the cursor is inside a ``` code block that hasn't been closed yet. */
function inOpenCodeFence(): boolean {
  const el = textarea.value;
  if (!el) return false;
  const beforeCursor = el.value.slice(0, el.selectionStart);
  return (beforeCursor.match(/^(```|~~~)/gm)?.length ?? 0) % 2 === 1;
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing && !inOpenCodeFence()) {
    e.preventDefault();
    submit();
  }
  if (e.key === "Escape" && attachments.value.length && !input.value) clearAttachments();
}

defineExpose({ focus: () => textarea.value?.focus(), attach, insertText });
</script>

<template>
  <form
    class="rounded-xl border bg-card shadow-xs transition-shadow focus-within:ring-3 focus-within:ring-ring/30"
    @submit.prevent="submit"
  >
    <ul v-if="attachments.length" class="flex flex-wrap gap-2 px-3 pt-3">
      <li
        v-for="a in attachments"
        :key="a.key"
        class="group/att relative flex h-16 max-w-56 items-center gap-2 overflow-hidden rounded-lg border bg-muted/50 pr-3"
      >
        <img v-if="a.thumb" :src="a.thumb" alt="" class="size-16 shrink-0 object-cover" />
        <div v-else class="flex size-16 shrink-0 items-center justify-center bg-muted text-muted-foreground">
          <FilmIcon v-if="mediaKind(a.file.type) === 'video'" class="size-6" />
          <FileTextIcon v-else class="size-6" />
        </div>
        <div class="min-w-0 text-xs">
          <div class="truncate font-medium">{{ a.file.name }}</div>
          <div class="text-muted-foreground">{{ formatSize(a.file.size) }}</div>
        </div>
        <button
          type="button"
          class="absolute top-1 right-1 rounded-full bg-background/80 p-0.5 text-muted-foreground hover:text-foreground"
          :aria-label="`Remove ${a.file.name}`"
          @click="detach(a.key)"
        >
          <XIcon class="size-3.5" />
        </button>
      </li>
    </ul>
    <textarea
      ref="textarea"
      v-model="input"
      rows="1"
      :placeholder="
        attachments.length ? 'Add a note (optional)…' : 'Paste a link or write a note… Markdown and #tags work'
      "
      class="block max-h-64 w-full resize-none bg-transparent px-4 pt-3 pb-1 text-sm outline-none placeholder:text-muted-foreground"
      @keydown="onKeydown"
      @paste="onPaste"
    />
    <div class="flex min-h-10 items-center gap-2 px-2 pb-2 pl-4">
      <div class="flex min-w-0 flex-1 flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
        <span v-if="preview?.url" class="flex min-w-0 items-center gap-1">
          <LinkIcon class="size-3 shrink-0" />
          <span class="truncate">{{ preview.url }}</span>
        </span>
        <span v-for="tag in preview?.tags" :key="tag" class="flex items-center">
          <HashIcon class="size-3" />{{ tag }}
        </span>
        <span v-if="!canSave" class="hidden sm:inline">Enter to save · Shift+Enter for a new line</span>
      </div>
      <input ref="picker" type="file" :accept="ACCEPT" multiple hidden @change="onPicked" />
      <Button type="button" size="icon-sm" variant="ghost" aria-label="Attach files" @click="picker?.click()">
        <PaperclipIcon />
      </Button>
      <Button type="submit" size="icon-sm" :disabled="!canSave" aria-label="Save">
        <SendHorizontalIcon />
      </Button>
    </div>
  </form>
</template>
