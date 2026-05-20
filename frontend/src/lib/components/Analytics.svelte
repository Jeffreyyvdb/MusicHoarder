<script lang="ts">
  import { env } from '$env/dynamic/public';

  const websiteId = env.PUBLIC_UMAMI_WEBSITE_ID;
  const scriptSrc = env.PUBLIC_UMAMI_SRC;
  const recorderSrc = env.PUBLIC_UMAMI_RECORDER_SRC;
  // When set (e.g. "/stats"), the tracker POSTs events here instead of the script's
  // origin — keeps /api/send same-origin so adblockers don't drop it.
  const hostUrl = env.PUBLIC_UMAMI_HOST_URL;

  const enabled = Boolean(websiteId && scriptSrc);
</script>

<svelte:head>
  {#if enabled}
    <script defer src={scriptSrc} data-website-id={websiteId} data-host-url={hostUrl}></script>
    {#if recorderSrc}
      <script
        defer
        src={recorderSrc}
        data-website-id={websiteId}
        data-host-url={hostUrl}
        data-sample-rate="1"
        data-mask-level="moderate"
        data-max-duration="300000"
      ></script>
    {/if}
  {/if}
</svelte:head>
