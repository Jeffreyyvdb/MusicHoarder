<script lang="ts">
  import { env } from '$env/dynamic/public';
  import { umamiEnabled } from '$lib/analytics/umami';

  const websiteId = env.PUBLIC_UMAMI_WEBSITE_ID;
  const scriptSrc = env.PUBLIC_UMAMI_SRC;
  const recorderSrc = env.PUBLIC_UMAMI_RECORDER_SRC;

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
