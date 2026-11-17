<script setup lang="ts">
import { LoaderCircleIcon } from "@lucide/vue";
import { useDark } from "@vueuse/core";

import HomeView from "@/components/HomeView.vue";
import LoginScreen from "@/components/LoginScreen.vue";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { authClient } from "@/lib/auth";

// Follows the system theme by toggling `.dark` on <html>.
useDark();
const session = authClient.useSession();
</script>

<template>
  <TooltipProvider :delay-duration="400">
    <div v-if="session.isPending" class="flex min-h-dvh items-center justify-center text-muted-foreground">
      <LoaderCircleIcon class="size-6 animate-spin" />
    </div>
    <HomeView v-else-if="session.data" :user="session.data.user" />
    <LoginScreen v-else />
  </TooltipProvider>
  <Toaster position="bottom-center" rich-colors close-button />
</template>
