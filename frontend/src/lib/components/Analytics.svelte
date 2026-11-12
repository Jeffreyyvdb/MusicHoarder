<script lang="ts">
  import {
    PUBLIC_UMAMI_WEBSITE_ID,
    PUBLIC_UMAMI_SRC,
    PUBLIC_UMAMI_RECORDER_SRC
  } from '$app/env/public';

  import { umamiEnabled } from '#lib/analytics/umami.ts';

  const websiteId = PUBLIC_UMAMI_WEBSITE_ID;
  const scriptSrc = PUBLIC_UMAMI_SRC;
  const recorderSrc = PUBLIC_UMAMI_RECORDER_SRC;

  // Shared with trackUmamiEvent, so a named event is only attempted where the tracker is loaded.
  const enabled = umamiEnabled();
</script>

<svelte:head>
  {#if enabled}
    <script defer src={scriptSrc} data-website-id={websiteId} data-performance="true"></script>
    {#if recorderSrc}
      <script
        defer
        src={recorderSrc}
        data-website-id={websiteId}
        data-sample-rate="1"
        data-mask-level="moderate"
        data-max-duration="300000"
      ></script>
    {/if}
  {/if}
</svelte:head>
