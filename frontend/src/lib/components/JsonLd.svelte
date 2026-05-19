<script lang="ts">
  type Props = { data: unknown };
  const { data }: Props = $props();

  function buildScriptTag(d: unknown): string {
    // Svelte's parser chokes on literal '<' inside a script-block string (even
    // in <script lang="ts">), breaking svelte-check. Build '<' via charCode
    // to bypass the parser. Also escape '</' inside the JSON payload to prevent
    // closing-tag breakout once the HTML reaches the browser.
    const lt = String.fromCharCode(60);
    const closeSeq = lt + '/';
    const escapedClose = lt + '\\/';
    const json = JSON.stringify(d).split(closeSeq).join(escapedClose);
    return lt + 'script type="application/ld+json">' + json + lt + '/script>';
  }
</script>

<svelte:head>{@html buildScriptTag(data)}</svelte:head>
