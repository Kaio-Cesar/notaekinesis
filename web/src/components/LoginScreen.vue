<script setup lang="ts">
import { LogInIcon, StickyNoteIcon } from "@lucide/vue";
import { onMounted, ref } from "vue";

import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { authClient } from "@/lib/auth";

const providerId = ref<string | null | undefined>(undefined);
const redirecting = ref(false);

onMounted(async () => {
  const { data } = await api.config.get();
  providerId.value = data?.providerId ?? null;
});

async function signIn() {
  if (!providerId.value) return;
  redirecting.value = true;
  // genericOAuth providers are registered as social providers in better-auth.
  const { error } = await authClient.signIn.social({ provider: providerId.value, callbackURL: "/" });
  if (error) redirecting.value = false;
}
</script>

<template>
  <main class="flex min-h-dvh items-center justify-center p-6">
    <div class="flex w-full max-w-sm flex-col items-center gap-6 text-center">
      <div class="flex size-12 items-center justify-center rounded-xl bg-primary text-primary-foreground">
        <StickyNoteIcon class="size-6" />
      </div>
      <div class="space-y-1">
        <h1 class="text-2xl font-semibold tracking-tight">notaekinesis</h1>
        <p class="text-sm text-muted-foreground">Your notes and links, everywhere.</p>
      </div>
      <Button class="w-full" size="lg" :disabled="!providerId || redirecting" @click="signIn">
        <LogInIcon />
        {{ redirecting ? "Redirecting…" : "Sign in with SSO" }}
      </Button>
      <p v-if="providerId === null" class="text-sm text-destructive">
        No identity provider is configured on the server. Set <code>OIDC_DISCOVERY_URL</code>.
      </p>
    </div>
  </main>
</template>
