<script setup lang="ts">
import {
  ArchiveIcon,
  InboxIcon,
  LayoutGridIcon,
  LayoutListIcon,
  LoaderCircleIcon,
  LogOutIcon,
  SearchIcon,
  StickyNoteIcon,
  XIcon,
} from "@lucide/vue";
import {
  breakpointsTailwind,
  onKeyStroke,
  useBreakpoints,
  useEventListener,
  useIntersectionObserver,
  useLocalStorage,
} from "@vueuse/core";
import { computed, nextTick, ref, useTemplateRef } from "vue";

import ItemCard from "@/components/ItemCard.vue";
import QuickAdd from "@/components/QuickAdd.vue";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useItems } from "@/composables/useItems";
import { authClient } from "@/lib/auth";
import { isUsefulDrag, readTransfer, type Transfer } from "@/lib/drop";
import { masonry } from "@/lib/masonry";
import { cn } from "@/lib/utils";

const props = defineProps<{ user: { name: string; email: string; image?: string | null } }>();

const initials = computed(() =>
  props.user.name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0])
    .join("")
    .toUpperCase(),
);

const { filters, items, tags, hasMore, loading, empty, loadMore, create, update, remove } = useItems();

function toggleTag(tag: string) {
  filters.tags = filters.tags.includes(tag) ? filters.tags.filter((t) => t !== tag) : [...filters.tags, tag];
}

async function signOut() {
  await authClient.signOut();
  window.location.reload();
}

// --- keyboard: "/" focuses search, "n" focuses the quick-add box --------------
const search = useTemplateRef<InstanceType<typeof Input>>("search");
const quickAdd = useTemplateRef<InstanceType<typeof QuickAdd>>("quickAdd");
const isTyping = (e: Event) =>
  e.target instanceof HTMLElement && e.target.closest("input, textarea, [contenteditable]");
const focusSearch = () => (search.value?.$el as HTMLInputElement | undefined)?.focus();
onKeyStroke("/", (e) => {
  if (isTyping(e)) return;
  e.preventDefault();
  focusSearch();
});
onKeyStroke("n", (e) => {
  if (isTyping(e) || e.ctrlKey || e.metaKey || e.altKey) return;
  e.preventDefault();
  quickAdd.value?.focus();
});

// --- drop or paste anywhere: it lands in the quick-add box, ready for a note ----
const dragging = ref(false);
let dragDepth = 0;

async function intoComposer({ files, text }: Transfer) {
  if (!files.length && !text) return;
  if (filters.archived) {
    filters.archived = false; // the composer lives in the inbox
    await nextTick();
  }
  if (files.length) quickAdd.value?.attach(files);
  if (text) await quickAdd.value?.insertText(text);
}

useEventListener(window, "dragenter", (e: DragEvent) => {
  if (!isUsefulDrag(e.dataTransfer)) return;
  dragDepth++;
  dragging.value = true;
});
useEventListener(window, "dragleave", () => {
  dragDepth = Math.max(0, dragDepth - 1);
  if (dragDepth === 0) dragging.value = false;
});
useEventListener(window, "dragover", (e: DragEvent) => e.preventDefault());
useEventListener(window, "drop", (e: DragEvent) => {
  e.preventDefault();
  dragDepth = 0;
  dragging.value = false;
  if (e.dataTransfer) intoComposer(readTransfer(e.dataTransfer));
});
useEventListener(window, "paste", (e: ClipboardEvent) => {
  // Pasting inside the composer is handled there; other fields keep normal paste.
  if (isTyping(e) || !e.clipboardData) return;
  e.preventDefault();
  intoComposer(readTransfer(e.clipboardData));
});

// --- list or masonry grid (remembered per browser) ------------------------------
const layout = useLocalStorage<"list" | "grid">("notaekinesis:layout", "list");
const breakpoints = useBreakpoints(breakpointsTailwind);
const columnCount = computed(() =>
  breakpoints.xl.value ? 4 : breakpoints.lg.value ? 3 : breakpoints.sm.value ? 2 : 1,
);
const columns = computed(() => masonry(items.value, columnCount.value));
const width = computed(() => (layout.value === "grid" ? "max-w-7xl" : "max-w-2xl"));

// --- infinite scroll ---------------------------------------------------------
const sentinel = useTemplateRef<HTMLElement>("sentinel");
useIntersectionObserver(sentinel, ([entry]) => entry?.isIntersecting && loadMore(), { rootMargin: "400px" });
</script>

<template>
  <div class="min-h-dvh">
    <header class="sticky top-0 z-10 border-b bg-background/80 backdrop-blur">
      <div :class="cn('mx-auto flex h-14 items-center gap-2 px-4', width)">
        <StickyNoteIcon class="size-5 shrink-0" />
        <div class="relative ml-2 flex-1">
          <SearchIcon
            class="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground"
          />
          <Input
            ref="search"
            v-model="filters.q"
            type="search"
            placeholder="Search  /"
            class="h-9 pl-8"
            @keydown.esc="filters.q = ''"
          />
        </div>
        <Tooltip>
          <TooltipTrigger as-child>
            <Button
              size="icon"
              variant="ghost"
              :aria-label="layout === 'grid' ? 'Show as list' : 'Show as grid'"
              @click="layout = layout === 'grid' ? 'list' : 'grid'"
            >
              <LayoutListIcon v-if="layout === 'grid'" />
              <LayoutGridIcon v-else />
            </Button>
          </TooltipTrigger>
          <TooltipContent>{{ layout === "grid" ? "List view" : "Grid view" }}</TooltipContent>
        </Tooltip>
        <Tooltip>
          <TooltipTrigger as-child>
            <Button
              size="icon"
              :variant="filters.archived ? 'secondary' : 'ghost'"
              :aria-pressed="filters.archived"
              aria-label="Show archive"
              @click="filters.archived = !filters.archived"
            >
              <ArchiveIcon />
            </Button>
          </TooltipTrigger>
          <TooltipContent>{{ filters.archived ? "Back to inbox" : "Archive" }}</TooltipContent>
        </Tooltip>
        <DropdownMenu>
          <DropdownMenuTrigger as-child>
            <Button size="icon" variant="ghost" class="rounded-full" aria-label="Account">
              <Avatar class="size-7">
                <AvatarImage v-if="user.image" :src="user.image" :alt="user.name" referrer-policy="no-referrer" />
                <AvatarFallback class="text-xs font-medium">{{ initials }}</AvatarFallback>
              </Avatar>
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuLabel class="font-normal">
              <div class="font-medium">{{ user.name }}</div>
              <div class="text-xs text-muted-foreground">{{ user.email }}</div>
            </DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuItem @select="signOut"><LogOutIcon /> Sign out</DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </header>

    <main :class="cn('mx-auto space-y-4 px-4 py-6', width)">
      <QuickAdd v-if="!filters.archived" ref="quickAdd" class="mx-auto max-w-2xl" :on-submit="create" />
      <h2 v-else class="mx-auto flex max-w-2xl items-center gap-2 text-sm font-medium text-muted-foreground">
        <ArchiveIcon class="size-4" /> Archive
      </h2>

      <div
        v-if="tags.length || filters.tags.length"
        :class="cn('flex flex-wrap items-center gap-1.5', layout === 'grid' && 'mx-auto max-w-2xl justify-center')"
      >
        <Badge
          v-for="{ tag, count } in tags"
          :key="tag"
          as="button"
          :variant="filters.tags.includes(tag) ? 'default' : 'outline'"
          class="cursor-pointer"
          @click="toggleTag(tag)"
        >
          #{{ tag }} <span class="opacity-60">{{ count }}</span>
        </Badge>
        <Button v-if="filters.tags.length" size="xs" variant="ghost" @click="filters.tags = []">
          <XIcon /> Clear
        </Button>
      </div>

      <div v-if="layout === 'grid'" class="flex items-start gap-3">
        <div v-for="(column, i) in columns" :key="i" class="flex min-w-0 flex-1 flex-col gap-3">
          <ItemCard
            v-for="item in column"
            :key="item.id"
            :item="item"
            :active-tags="filters.tags"
            compact
            @update="(patch) => update(item, patch)"
            @remove="remove(item)"
            @toggle-tag="toggleTag"
          />
        </div>
      </div>
      <TransitionGroup v-else tag="div" class="space-y-2" move-class="transition-transform duration-200">
        <ItemCard
          v-for="item in items"
          :key="item.id"
          :item="item"
          :active-tags="filters.tags"
          @update="(patch) => update(item, patch)"
          @remove="remove(item)"
          @toggle-tag="toggleTag"
        />
      </TransitionGroup>

      <div ref="sentinel" class="flex justify-center py-4 text-muted-foreground">
        <LoaderCircleIcon v-if="loading" class="size-5 animate-spin" />
        <Button v-else-if="hasMore" variant="ghost" size="sm" @click="loadMore">Load more</Button>
      </div>

      <div v-if="empty" class="flex flex-col items-center gap-2 py-16 text-center text-sm text-muted-foreground">
        <InboxIcon class="size-8" />
        <template v-if="filters.q || filters.tags.length">Nothing matches.</template>
        <template v-else-if="filters.archived">Nothing archived yet.</template>
        <template v-else>Nothing saved yet. Paste or drop a link, image, video or PDF anywhere on this page.</template>
      </div>
    </main>

    <Transition
      enter-active-class="transition-opacity duration-150"
      leave-active-class="transition-opacity duration-150"
      enter-from-class="opacity-0"
      leave-to-class="opacity-0"
    >
      <div
        v-if="dragging"
        class="pointer-events-none fixed inset-0 z-50 flex items-center justify-center bg-background/70 backdrop-blur-sm"
      >
        <div class="rounded-2xl border-2 border-dashed border-primary/50 px-10 py-8 text-lg font-medium">
          Drop to add
        </div>
      </div>
    </Transition>
  </div>
</template>
